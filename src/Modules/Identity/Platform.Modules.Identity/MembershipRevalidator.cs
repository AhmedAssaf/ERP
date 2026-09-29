using System.Data.Common;
using System.Security.Claims;
using Microsoft.Extensions.Logging;
using Platform.Modules.Audit.Contracts;
using Platform.Modules.Identity.Contracts;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Identity;

/// <summary>
/// The Identity module's <see cref="IMembershipRevalidation"/> (W-21), scoped to a request or circuit for its tenant and
/// audit writer; what it learns lives in the process-wide <see cref="MembershipEvidence"/>.
/// <list type="bullet">
/// <item>The newest known fact decides while it is fresh: a confirmed membership for <see cref="MemberFor"/>, a seen
/// removal for <see cref="RemovedFor"/>. A sign-in counts as a membership confirmed at its time, since Keycloak put the
/// host tenant's organization in that token (the narrowed <c>organization:&lt;alias&gt;</c> scope).</item>
/// <item>Otherwise Keycloak is asked once for all concurrent callers, with a <see cref="CheckTimeout"/> of its own. A
/// removal or a disabled account is recorded and audited once as <c>identity.session_revoked</c> in the tenant's log; if
/// that audit fails the fact is dropped, so the next check audits it.</item>
/// <item>When Keycloak cannot answer it is not asked again for <see cref="Backoff"/> (so an outage does not add a timeout to
/// every request), and a membership confirmed within <see cref="Grace"/> keeps the session; older, or never confirmed,
/// ends it. The reason: an outage stops new sign-ins anyway, so a short grace lets people finish what they are doing
/// during a blip, while a long outage must not keep a removed member in (the removal needs Keycloak up, so the exposure is
/// a removal made just before Keycloak went down, for at most <see cref="Grace"/>).</item>
/// </list>
/// Worst case for a removal while Keycloak is up: <see cref="MemberFor"/> on the next HTTP request, and one circuit
/// revalidation interval more (one minute in the web host) in an open circuit; both under the five minutes of W-21.
/// </summary>
internal sealed partial class MembershipRevalidator(
    ITenantAccessor tenants,
    MembershipEvidence evidence,
    IOrganizationMembershipSource source,
    IAuditWriter audit,
    TimeProvider clock,
    ILogger<MembershipRevalidator> logger) : IMembershipRevalidation
{
    internal static readonly TimeSpan MemberFor = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan RemovedFor = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan Grace = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan Backoff = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(5);

    public const string AuditAction = "identity.session_revoked";

    public async Task<bool> IsStillMemberAsync(ClaimsPrincipal user, DateTimeOffset? signedInAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        var tenant = tenants.Current;
        if (tenant is null || user.Identity?.IsAuthenticated != true
            || user.FindFirst(IdentityClaims.Subject)?.Value is not { Length: > 0 } userId
            || !OrganizationClaims.BelongsTo(user, tenant))
        {
            return true;
        }

        var alias = tenant.KeycloakOrgAlias;
        if (signedInAt is { } signIn)
        {
            evidence.Record(alias, userId, new MembershipFact(Member: true, signIn));
        }

        var now = clock.GetUtcNow();
        var known = evidence.Latest(alias, userId);
        if (known is { Member: true } confirmed && now - confirmed.At < MemberFor)
        {
            return true;
        }

        if (known is { Member: false } removed && now - removed.At < RemovedFor)
        {
            return false;
        }

        if (!evidence.KeycloakQuiet(now))
        {
            var session = user.HasClaim(IdentityClaims.Roles, IdentityClaims.VendorRealmRole) ? "vendor" : "staff";
            var answer = await evidence.CheckOnceAsync(alias, userId, () => CheckAndRecordAsync(tenant, userId, session), cancellationToken);
            switch (answer)
            {
                case OrganizationMembership.Member:
                    return true;
                case OrganizationMembership.NotMember or OrganizationMembership.Disabled:
                    return false;
            }
        }

        // Keycloak did not answer, now or within the backoff: fall back on the newest confirmation, bounded by the grace.
        known = evidence.Latest(alias, userId);
        if (known is { Member: true } lastConfirmed && now - lastConfirmed.At < Grace)
        {
            KeptInGrace(logger, tenant.Slug, userId);
            return true;
        }

        EndedUnverified(logger, tenant.Slug, userId);
        return false;
    }

    private async Task<OrganizationMembership> CheckAndRecordAsync(TenantContext tenant, string userId, string session)
    {
        OrganizationMembership answer;
        using (var timeout = new CancellationTokenSource(CheckTimeout))
        {
            try
            {
                answer = await source.CheckAsync(tenant.KeycloakOrgAlias, userId, timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                answer = OrganizationMembership.Unavailable;
            }
        }

        var at = clock.GetUtcNow();
        switch (answer)
        {
            case OrganizationMembership.Member:
                evidence.Record(tenant.KeycloakOrgAlias, userId, new MembershipFact(Member: true, at));
                break;
            case OrganizationMembership.NotMember or OrganizationMembership.Disabled:
                var removal = new MembershipFact(Member: false, at);
                evidence.Record(tenant.KeycloakOrgAlias, userId, removal);
                await AuditRemovalAsync(tenant, userId, session, answer, removal);
                break;
            default:
                evidence.QuietKeycloakUntil(at + Backoff);
                break;
        }

        return answer;
    }

    private async Task AuditRemovalAsync(TenantContext tenant, string userId, string session, OrganizationMembership answer, MembershipFact removal)
    {
        var reason = answer == OrganizationMembership.Disabled ? "account_disabled" : "removed_from_organization";
        try
        {
            // The actor is the session's own user: a vendor circuit writes the tenant's log only as itself (audit 0003).
            await audit.WriteAsync(
                new AuditEntry(
                    userId,
                    AuditAction,
                    "user",
                    userId,
                    new Dictionary<string, string?>
                    {
                        ["organization"] = tenant.KeycloakOrgAlias,
                        ["reason"] = reason,
                        ["session"] = session,
                    }),
                CancellationToken.None);
            SessionRevoked(logger, tenant.Slug, userId, reason);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            // The session still ends; the fact is dropped so the next check asks again and audits then.
            evidence.Forget(tenant.KeycloakOrgAlias, userId, removal);
            AuditFailed(logger, tenant.Slug, userId, ex.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Session of user {UserId} on tenant {Tenant} ended: {Reason}.")]
    private static partial void SessionRevoked(ILogger logger, string tenant, string userId, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "The end of user {UserId}'s session on tenant {Tenant} was not audited ({ErrorType}); the next check retries.")]
    private static partial void AuditFailed(ILogger logger, string tenant, string userId, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Keycloak could not confirm user {UserId} on tenant {Tenant}; the session is kept within the grace period.")]
    private static partial void KeptInGrace(ILogger logger, string tenant, string userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Keycloak could not confirm user {UserId} on tenant {Tenant} and no confirmation is recent enough; the session ends.")]
    private static partial void EndedUnverified(ILogger logger, string tenant, string userId);
}
