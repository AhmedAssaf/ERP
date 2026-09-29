using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Identity.Keycloak;
using Platform.Modules.Identity.Members;
using Platform.Shared;
using Platform.Shared.Data;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Identity;

public static class IdentityModule
{
    /// <summary>
    /// Authenticated and a member of the host tenant's organization, staff or vendor. The Vendor policy builds on it; staff
    /// pages use <see cref="TenantStaffPolicy"/>.
    /// </summary>
    public static AuthorizationPolicy SameTenantPolicy { get; } = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .AddRequirements(new SameTenantRequirement())
        .Build();

    /// <summary>
    /// <see cref="SameTenantPolicy"/> for staff only: a principal holding the realm role <c>vendor</c> is refused, whatever
    /// member row it has. Vendors sign in without a second factor (V-4), so a vendor role must never open a staff page.
    /// The host uses it as the default and fallback policy on tenant hosts; the four role policies include it.
    /// </summary>
    public static AuthorizationPolicy TenantStaffPolicy { get; } = new AuthorizationPolicyBuilder()
        .Combine(SameTenantPolicy)
        .RequireAssertion(NotVendor)
        .Build();

    /// <summary>Realm role of the platform realm (<c>waslabid-platform</c>) that opens the platform console.</summary>
    public const string PlatformAdminRole = "platform-admin";

    /// <summary>The acr level of an OTP login in the platform realm (D-2); a password-only login is level 1.</summary>
    public const int PlatformMinimumAcr = 2;

    /// <summary>
    /// The requirements of the PlatformAdmin policy (spec 3.1, D-2), without an authentication scheme: the web host binds
    /// them to its platform cookie. Authenticated, holding the platform-admin realm role, and signed in at acr level 2 or
    /// higher, so a password-only session is refused even if the realm's flow drifts.
    /// </summary>
    public static AuthorizationPolicy PlatformAdminRequirements { get; } = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireClaim(IdentityClaims.Roles, PlatformAdminRole)
        .AddRequirements(new MinimumAcrRequirement(PlatformMinimumAcr))
        .Build();

    /// <summary>
    /// The four tenant role policies (F-07, spec 4.1) by name (<see cref="TenantPolicies"/>): signed in, a member of the
    /// host tenant's organization, not holding the realm role <c>vendor</c>, and holding the role in the host tenant
    /// according to <c>identity.members</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, AuthorizationPolicy> TenantRolePolicies { get; } = new Dictionary<string, AuthorizationPolicy>
    {
        [TenantPolicies.TenantAdmin] = RolePolicy(TenantRoles.TenantAdmin),
        [TenantPolicies.ContractsOfficer] = RolePolicy(TenantRoles.ContractsOfficer),
        [TenantPolicies.TechnicalEvaluator] = RolePolicy(TenantRoles.TechnicalEvaluator),
        [TenantPolicies.FinanceApprover] = RolePolicy(TenantRoles.FinanceApprover),
    };

    /// <summary>
    /// A tenant role policy that passes for any one of <paramref name="roles"/>: signed in, a member of the host tenant's
    /// organization, not holding the realm role <c>vendor</c>, and holding one of the roles in the host tenant according to
    /// <c>identity.members</c>. A member holding none of them is audited as <c>identity.role_denied</c>, as for one role.
    /// </summary>
    public static AuthorizationPolicy AnyTenantRolePolicy(params IReadOnlyList<string> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        var unknown = roles.Where(r => !TenantRoles.All.Contains(r, StringComparer.Ordinal)).ToList();
        if (roles.Count == 0 || unknown.Count > 0)
        {
            throw new ArgumentException($"Name one or more tenant roles; not tenant roles: {string.Join(", ", unknown)}.", nameof(roles));
        }

        return new AuthorizationPolicyBuilder()
            .Combine(TenantStaffPolicy)
            .AddRequirements(new TenantRoleRequirement([.. roles]))
            .Build();
    }

    public static IServiceCollection AddIdentityModule(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();
        services.AddModuleDbContext<MembersDbContext>(connectionString);
        services.AddScoped<MemberDirectory>();
        services.AddScoped<IMemberDirectory>(sp => sp.GetRequiredService<MemberDirectory>());
        services.TryAddSingleton<MemberRolesCache>();
        services.AddScoped<IClaimsTransformation, MembersClaimsTransformation>();
        services.TryAddSingleton<DenialAuditThrottle>();
        services.AddScoped<IAuthorizationHandler, SameTenantHandler>();
        services.AddScoped<IAuthorizationHandler, TenantRoleHandler>();
        services.TryAddSingleton<IOrganizationMemberSource, UnavailableOrganizationMembers>();
        services.TryAddSingleton<IOrganizationMembers, CachingOrganizationMembers>();
        services.TryAddScoped<IVendorAccounts, UnavailableVendorAccounts>();
        // W-21: sessions revalidated against Keycloak organization membership; AddKeycloakAdmin supplies the answers.
        services.TryAddSingleton<MembershipEvidence>();
        services.TryAddSingleton<IOrganizationMembershipSource, UnavailableOrganizationMembership>();
        services.TryAddSingleton<RevocationAuditLog>();
        services.AddScoped<RevocationAuditWriter>();
        services.AddScoped<IMembershipRevalidation>(sp =>
            ActivatorUtilities.CreateInstance<MembershipRevalidator>(sp, sp.GetRequiredService<RevocationAuditWriter>()));
        services.AddHostedService<RevocationAuditRetry>();
        return services;
    }

    /// <summary>
    /// Registers the Keycloak Admin API client (settings <c>KeycloakAdmin:*</c>, plan task 9, spec D-4), the staff service
    /// that invites through it (<see cref="IStaffService"/>, F-06), and the client as the source of organization member
    /// counts and of membership answers (W-21) in place of the placeholders. Call after <see cref="AddIdentityModule"/>.
    /// Without <c>KeycloakAdmin:BaseUrl</c> and <c>KeycloakAdmin:ClientSecret</c> counts stay unknown, invitations fail with
    /// a logged error, and membership cannot be revalidated, so tenant sessions end once their sign-in is older than the
    /// revalidation grace period.
    /// </summary>
    public static IServiceCollection AddKeycloakAdmin(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<KeycloakAdminOptions>().Bind(configuration.GetSection(KeycloakAdminOptions.Section));
        services.TryAddSingleton<KeycloakAdminState>();
        services.AddHttpClient<KeycloakAdminClient>((sp, http) =>
        {
            var baseUrl = sp.GetRequiredService<IOptions<KeycloakAdminOptions>>().Value.BaseUrl;
            if (!string.IsNullOrWhiteSpace(baseUrl))
            {
                http.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
            }

            http.Timeout = TimeSpan.FromSeconds(15);
        });
        services.Replace(ServiceDescriptor.Singleton<IOrganizationMemberSource, KeycloakOrganizationMemberSource>());
        services.Replace(ServiceDescriptor.Singleton<IOrganizationMembershipSource, KeycloakOrganizationMembershipSource>());
        // The notice to an invited person whose account needs no setup goes out through our own SMTP sender (Smtp:*),
        // in both languages from this module's resources.
        services.AddEmail(configuration);
        services.AddLocalization(o => o.ResourcesPath = "Resources");
        services.TryAddSingleton<InvitationNotice>();
        services.AddScoped<IStaffService, StaffService>();
        // Vendor slice (V-3): the realm role vendor and organization membership for registered vendor companies.
        services.Replace(ServiceDescriptor.Scoped<IVendorAccounts, KeycloakVendorAccounts>());
        return services;
    }

    /// <summary>Registers <see cref="TenantRolePolicies"/> under their names.</summary>
    public static AuthorizationOptions AddTenantRolePolicies(this AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (var (name, policy) in TenantRolePolicies)
        {
            options.AddPolicy(name, policy);
        }

        return options;
    }

    /// <summary>
    /// Whether the principal's token carried the Keycloak organization of <paramref name="tenant"/> (matched on the alias,
    /// spec section 7), the membership the SameTenant requirement checks. The host uses it to tell a refusal for a missing
    /// membership (the access-removed page, W-21) from a refusal for a missing role.
    /// </summary>
    public static bool ClaimsOrganizationOf(ClaimsPrincipal principal, TenantContext tenant)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(tenant);
        return OrganizationClaims.BelongsTo(principal, tenant);
    }

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlConnection connection, CancellationToken cancellationToken = default) =>
        SqlMigrator.ApplyAsync(connection, "identity", typeof(IdentityModule).Assembly, cancellationToken);

    private static AuthorizationPolicy RolePolicy(string role) => new AuthorizationPolicyBuilder()
        .Combine(TenantStaffPolicy)
        .AddRequirements(new TenantRoleRequirement(role))
        .Build();

    // The realm role as the token carries it, on any identity of the principal.
    private static bool NotVendor(AuthorizationHandlerContext context) =>
        !context.User.HasClaim(IdentityClaims.Roles, IdentityClaims.VendorRealmRole);
}
