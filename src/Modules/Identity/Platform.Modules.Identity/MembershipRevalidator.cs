using System.Data.Common;
using System.Security.Claims;
using Microsoft.Extensions.Logging;
using Platform.Modules.Audit.Contracts;
using Platform.Modules.Identity.Contracts;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Identity;

/// <summary>
/// The Identity module's <see cref="IMembershipRevalidation"/> (W-21), scoped to a request or circuit for its tenant; what
/// it learns lives in the process-wide <see cref="MembershipEvidence"/>.
/// <list type="bullet">
/// <item>What is checked: a session that claims the host tenant's organization is checked against that organization
/// (member and enabled); any other signed-in session on a tenant host (an applicant, a vendor on another tenant's join
/// page, a cookie replayed on another host) against the account itself (exists and enabled, P-2), in the
/// <see cref="MembershipEvidence.AccountScope"/>.</item>
/// <item>The newest known fact decides while it is fresh: a confirmation for <see cref="MemberFor"/>, a seen removal for
/// <see cref="RemovedFor"/>. A sign-in counts as a confirmation at its time, since Keycloak put the host tenant's
/// organization in that token (the narrowed <c>organization:&lt;alias&gt;</c> scope) and issues no token to a disabled
/// account.</item>
/// <item>Otherwise Keycloak is asked once for all concurrent callers, with a <see cref="CheckTimeout"/> of its own. A
/// removal, a disabled or a deleted account is recorded and audited once as <c>identity.session_revoked</c> in the host
/// tenant's log. The audit writer the host wires writes from a scope of its own and queues a failed write for retry
/// (<see cref="RevocationAuditWriter"/>), so the end of a session is never unaudited; should the writer still throw, the
/// fact is dropped so the next check audits it.</item>
/// <item>When Keycloak cannot answer about a scope it is not asked about that scope again for <see cref="Backoff"/>, so an
/// outage does not add a timeout to every request while other organizations keep being checked, and a confirmation
/// within <see cref="Grace"/> keeps the session; older, or never confirmed, ends it. The grace is short on purpose: a
/// removal followed by a Keycloak failure keeps access for at most <see cref="Grace"/>, plus one circuit revalidation
/// interval and one <see cref="CheckTimeout"/> in an open circuit, which stays under the five minutes of W-21. The cost:
/// when Keycloak is down for longer than that, sessions end on their next request (nobody can sign in either).</item>
/// </list>
/// Worst case for a removal while Keycloak is up: <see cref="MemberFor"/> on the next HTTP request, and one circuit
/// revalidation interval more (one minute in the web host) in an open circuit.
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
    internal static readonly TimeSpan Grace = TimeSpan.FromSeconds(210);
    internal static readonly TimeSpan Backoff = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(5);

    public const string AuditAction = "identity.session_revoked";

    public async Task<bool> IsStillMemberAsync(ClaimsPrincipal user, DateTimeOffset? signedInAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        var tenant = tenants.Current;
        if (tenant is null || user.Identity?.IsAuthenticated != true
            || user.FindFirst(IdentityClaims.Subject)?.Value is not { Length: > 0 } userId)
        {
            return true;
        }

        var scope = OrganizationClaims.BelongsTo(user, tenant) ? tenant.KeycloakOrgAlias : MembershipEvidence.AccountScope;
        if (signedInAt is { } signIn)
        {
            evidence.Record(scope, userId, new MembershipFact(Member: true, signIn));
        }

        var now = clock.GetUtcNow();
        var known = evidence.Latest(scope, userId);
        if (known is { Member: true } confirmed && now - confirmed.At < MemberFor)
        {
            return true;
        }

        if (known is { Member: false } removed && now - removed.At < RemovedFor)
        {
            return false;
        }

        if (!evidence.KeycloakQuiet(scope, now))
        {
            var session = user.HasClaim(IdentityClaims.Roles, IdentityClaims.VendorRealmRole) ? "vendor" : "staff";
            var answer = await evidence.CheckOnceAsync(scope, userId, () => CheckAndRecordAsync(tenant, scope, userId, session), cancellationToken);
            switch (answer)
            {
                case OrganizationMembership.Member:
                    return true;
                case OrganizationMembership.NotMember or OrganizationMembership.Disabled:
                    return false;
            }
        }

        // Keycloak did not answer, now or within the back-off: fall back on the newest confirmation, bounded by the grace.
        known = evidence.Latest(scope, userId);
        if (known is { Member: true } lastConfirmed && now - lastConfirmed.At < Grace)
        {
            KeptInGrace(logger, tenant.Slug, userId);
            return true;
        }

        EndedUnverified(logger, tenant.Slug, userId);
        return false;
    }

    public bool ClaimsHostOrganization(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return tenants.Current is { } tenant && OrganizationClaims.BelongsTo(user, tenant);
    }

    private async Task<OrganizationMembership> CheckAndRecordAsync(TenantContext tenant, string scope, string userId, string session)
    {
        OrganizationMembership answer;
        using (var timeout = new CancellationTokenSource(CheckTimeout))
        {
            try
            {
                answer = scope == MembershipEvidence.AccountScope
                    ? await source.CheckAccountAsync(userId, timeout.Token)
                    : await source.CheckAsync(scope, userId, timeout.Token);
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
                evidence.Record(scope, userId, new MembershipFact(Member: true, at));
                break;
            case OrganizationMembership.NotMember or OrganizationMembership.Disabled:
                var removal = new MembershipFact(Member: false, at);
                evidence.Record(scope, userId, removal);
                await AuditRemovalAsync(tenant, scope, userId, session, answer, removal);
                break;
            default:
                evidence.QuietKeycloakUntil(scope, at + Backoff);
                break;
        }

        return answer;
    }

    private async Task AuditRemovalAsync(
        TenantContext tenant, string scope, string userId, string session, OrganizationMembership answer, MembershipFact removal)
    {
        var accountScope = scope == MembershipEvidence.AccountScope;
        var reason = answer == OrganizationMembership.Disabled ? "account_disabled"
            : accountScope ? "account_removed"
            : "removed_from_organization";
        var data = new Dictionary<string, string?> { ["reason"] = reason, ["session"] = session };
        if (!accountScope)
        {
            data["organization"] = scope;
        }

        try
        {
            // The actor is the session's own user: a vendor circuit writes the tenant's log only as itself (audit 0003).
            await audit.WriteAsync(new AuditEntry(userId, AuditAction, "user", userId, data), CancellationToken.None);
            SessionRevoked(logger, tenant.Slug, userId, reason);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            // The session still ends; the fact is dropped so the next check asks again and audits then.
            evidence.Forget(scope, userId, removal);
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
