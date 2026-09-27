using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Platform.Modules.Audit.Contracts;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Identity.Members;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Identity;

/// <summary>The user holds at least one of <see cref="Roles"/> in the host tenant (F-07).</summary>
internal sealed class TenantRoleRequirement(params IReadOnlyList<string> roles) : IAuthorizationRequirement
{
    public IReadOnlyList<string> Roles { get; } = roles.Count > 0 ? roles : throw new ArgumentException("At least one role is required.", nameof(roles));
}

/// <summary>
/// Passes when the members identity (<see cref="MembersClaimsTransformation"/>) carries one of the roles; role claims on any other
/// identity count for nothing. A member of the host tenant without any of them is audited as <c>identity.role_denied</c>,
/// once per user, host and path per minute (<see cref="DenialAuditThrottle"/>). A user outside the tenant is left to the
/// same-tenant check, which audits that case, so one attempt is never logged twice.
/// </summary>
internal sealed class TenantRoleHandler(
    ITenantAccessor tenants, IAuditWriter audit, DenialAuditThrottle throttle, IHttpContextAccessor httpContextAccessor)
    : AuthorizationHandler<TenantRoleRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, TenantRoleRequirement requirement)
    {
        var tenant = tenants.Current;
        if (tenant is null || context.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        var holds = context.User.Identities.Any(i =>
            i.AuthenticationType == MembersClaimsTransformation.AuthenticationType
            && requirement.Roles.Any(role => i.HasClaim(IdentityClaims.Role, role)));
        if (holds)
        {
            context.Succeed(requirement);
            return;
        }

        if (!OrganizationClaims.BelongsTo(context.User, tenant))
        {
            return;
        }

        var request = DenialRequest.From(context, httpContextAccessor, tenant);
        var userId = context.User.FindFirst(IdentityClaims.Subject)?.Value;
        if (!throttle.ShouldAudit(new DenialKey(tenant.TenantId, "identity.role_denied", userId, request.Host, request.Path)))
        {
            return;
        }

        await audit.WriteAsync(
            new AuditEntry(userId, "identity.role_denied", "path", request.Path, new Dictionary<string, string?>
            {
                ["role"] = string.Join(",", requirement.Roles),
                ["host"] = request.Host,
            }),
            request.Aborted);
    }
}

/// <summary>Host and path of the request being authorized; inside a Blazor circuit there is none, so both are empty.</summary>
internal readonly record struct DenialRequest(string Host, string Path, CancellationToken Aborted)
{
    public static DenialRequest From(AuthorizationHandlerContext context, IHttpContextAccessor accessor, TenantContext tenant)
    {
        var http = context.Resource as HttpContext ?? accessor.HttpContext;
        var host = http?.Request.Host.Host;
        return new DenialRequest(
            string.IsNullOrEmpty(host) ? tenant.Slug : host.ToLowerInvariant(),
            http?.Request.Path.Value ?? string.Empty,
            http?.RequestAborted ?? CancellationToken.None);
    }
}
