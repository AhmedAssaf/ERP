using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Identity.Members;
using Platform.Shared.Tenancy;
using Platform.Web.Account;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>
/// The sessions of spec 6.2 as the web host holds them after authentication and the context middleware (or the circuit
/// handlers): the principal with the claims Keycloak and the members claims transformation give it, and the scope's
/// tenant, vendor context and platform mark.
/// </summary>
internal sealed record UsageSession(ClaimsPrincipal User, TenantAccessor Tenants, VendorAccessor Vendor, PlatformRequestContext Platform)
{
    public string? Subject => User.FindFirst(IdentityClaims.Subject)?.Value;

    /// <summary>A staff member of the tenant: in its organization, with a role in identity.members (the members identity).</summary>
    public static UsageSession Staff(string subject, TenantContext tenant, params string[] roles) =>
        OnTenant(Principal(subject, [tenant.KeycloakOrgAlias], realmRoles: [], memberRoles: roles.Length == 0 ? [TenantRoles.TenantAdmin] : roles), tenant);

    /// <summary>A vendor user of the tenant under the Vendor policy: the realm role, the organization and a vendor context.</summary>
    public static UsageSession VendorUser(string subject, TenantContext tenant, Guid companyId)
    {
        var session = OnTenant(Principal(subject, [tenant.KeycloakOrgAlias], realmRoles: [IdentityClaims.VendorRealmRole]), tenant);
        session.Vendor.Set(new VendorContext(companyId));
        return session;
    }

    /// <summary>A platform admin on the platform host (the PlatformAdmin policy: the role and acr 2).</summary>
    public static UsageSession PlatformAdmin(string subject, string acr = "2")
    {
        var session = new UsageSession(
            Principal(subject, [], realmRoles: ["platform-admin"], acr: acr), new TenantAccessor(), new VendorAccessor(), new PlatformRequestContext());
        session.Platform.MarkPlatform();
        return session;
    }

    public static UsageSession Anonymous(TenantContext tenant) => OnTenant(new ClaimsPrincipal(new ClaimsIdentity()), tenant);

    public static UsageSession OnTenant(ClaimsPrincipal user, TenantContext tenant)
    {
        var tenants = new TenantAccessor();
        tenants.Set(tenant);
        return new UsageSession(user, tenants, new VendorAccessor(), new PlatformRequestContext());
    }

    public static ClaimsPrincipal Principal(
        string subject, IReadOnlyList<string> organizations, IReadOnlyList<string> realmRoles, IReadOnlyList<string>? memberRoles = null, string? acr = null)
    {
        var claims = new List<Claim>
        {
            new(IdentityClaims.Subject, subject),
            new(IdentityClaims.Username, subject),
            new(IdentityClaims.Email, $"{subject}@usage.test"),
        };
        claims.AddRange(organizations.Select(o => new Claim(IdentityClaims.Organization, o)));
        claims.AddRange(realmRoles.Select(r => new Claim(IdentityClaims.Roles, r)));
        if (acr is not null)
        {
            claims.Add(new Claim(IdentityClaims.Acr, acr));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "AuthenticationTypes.Federation", IdentityClaims.Username, IdentityClaims.Roles));
        if (memberRoles is not null)
        {
            principal.AddIdentity(new ClaimsIdentity(
                memberRoles.Select(r => new Claim(IdentityClaims.Role, r)), MembersClaimsTransformation.AuthenticationType, IdentityClaims.Subject, IdentityClaims.Role));
        }

        return principal;
    }

    /// <summary>
    /// The connection request of a circuit opened by this session (the <c>/_blazor</c> request). Held in a field, not in
    /// <see cref="HttpContextAccessor"/>, whose instances share one async-local slot, so several circuits in one test keep
    /// their own connection.
    /// </summary>
    public IHttpContextAccessor Connection() => new FixedConnection(new DefaultHttpContext { User = User });

    /// <summary>A W-21 guard for the circuit, as the host registers one per circuit scope.</summary>
    public CircuitSessionGuard Guard() =>
        new(new FixedNavigation(), Connection(), NullLogger<CircuitSessionGuard>.Instance);

    private sealed class FixedConnection(HttpContext context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = context;
    }

    private sealed class FixedNavigation : NavigationManager
    {
        public FixedNavigation() => Initialize("https://usage.localhost/", "https://usage.localhost/");
    }
}
