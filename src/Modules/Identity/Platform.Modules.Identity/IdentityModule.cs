using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Identity.Members;
using Platform.Shared.Data;

namespace Platform.Modules.Identity;

public static class IdentityModule
{
    /// <summary>Authenticated and a member of the host tenant's organization. The host uses it as the fallback policy.</summary>
    public static AuthorizationPolicy SameTenantPolicy { get; } = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .AddRequirements(new SameTenantRequirement())
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
    /// host tenant's organization, and holding the role in the host tenant according to <c>identity.members</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, AuthorizationPolicy> TenantRolePolicies { get; } = new Dictionary<string, AuthorizationPolicy>
    {
        [TenantPolicies.TenantAdmin] = RolePolicy(TenantRoles.TenantAdmin),
        [TenantPolicies.ContractsOfficer] = RolePolicy(TenantRoles.ContractsOfficer),
        [TenantPolicies.TechnicalEvaluator] = RolePolicy(TenantRoles.TechnicalEvaluator),
        [TenantPolicies.FinanceApprover] = RolePolicy(TenantRoles.FinanceApprover),
    };

    public static IServiceCollection AddIdentityModule(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();
        services.AddModuleDbContext<MembersDbContext>(connectionString);
        services.AddScoped<MemberDirectory>();
        services.AddScoped<IMemberDirectory>(sp => sp.GetRequiredService<MemberDirectory>());
        services.AddScoped<IClaimsTransformation, MembersClaimsTransformation>();
        services.TryAddSingleton<DenialAuditThrottle>();
        services.AddScoped<IAuthorizationHandler, SameTenantHandler>();
        services.AddScoped<IAuthorizationHandler, TenantRoleHandler>();
        services.TryAddSingleton<IOrganizationMemberSource, UnavailableOrganizationMembers>();
        services.TryAddSingleton<IOrganizationMembers, CachingOrganizationMembers>();
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

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlConnection connection, CancellationToken cancellationToken = default) =>
        SqlMigrator.ApplyAsync(connection, "identity", typeof(IdentityModule).Assembly, cancellationToken);

    private static AuthorizationPolicy RolePolicy(string role) => new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .AddRequirements(new SameTenantRequirement(), new TenantRoleRequirement(role))
        .Build();
}
