using Microsoft.AspNetCore.Authorization;
using Platform.Modules.Audit.Contracts;
using Platform.Modules.Identity.Contracts;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Identity;

internal sealed class SameTenantRequirement : IAuthorizationRequirement;

/// <summary>The signed-in user must be a member of the Keycloak organization of the host's tenant (W-04).</summary>
internal sealed class SameTenantHandler(ITenantAccessor tenants, IAuditWriter audit) : AuthorizationHandler<SameTenantRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, SameTenantRequirement requirement)
    {
        var tenant = tenants.Current;
        if (tenant is null || context.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        if (OrganizationClaims.BelongsTo(context.User, tenant))
        {
            context.Succeed(requirement);
            return;
        }

        await audit.WriteAsync(new AuditEntry(
            context.User.FindFirst(IdentityClaims.Subject)?.Value,
            "identity.cross_tenant_denied",
            "tenant",
            tenant.Slug,
            new Dictionary<string, string?> { ["organizations"] = string.Join(',', OrganizationClaims.Aliases(context.User)) }));
    }
}
