using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Platform.Modules.Audit.Contracts;
using Platform.Modules.Identity.Contracts;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Identity;

internal sealed class SameTenantRequirement : IAuthorizationRequirement;

/// <summary>
/// The signed-in user must be a member of the Keycloak organization of the host's tenant (W-04). A denial is audited as
/// <c>identity.cross_tenant_denied</c> once per user, host and path per minute (W-27, <see cref="DenialAuditThrottle"/>).
/// </summary>
internal sealed class SameTenantHandler(
    ITenantAccessor tenants, IAuditWriter audit, DenialAuditThrottle throttle, IHttpContextAccessor httpContextAccessor)
    : AuthorizationHandler<SameTenantRequirement>
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

        var request = DenialRequest.From(context, httpContextAccessor, tenant);
        var userId = context.User.FindFirst(IdentityClaims.Subject)?.Value;
        if (!throttle.ShouldAudit(new DenialKey(tenant.TenantId, "identity.cross_tenant_denied", userId, request.Host, request.Path)))
        {
            return;
        }

        // The row lands in the attacked tenant's audit log, so it names the attempted host only, never the intruder's
        // other organizations.
        await audit.WriteAsync(new AuditEntry(userId, "identity.cross_tenant_denied", "host", request.Host), request.Aborted);
    }
}
