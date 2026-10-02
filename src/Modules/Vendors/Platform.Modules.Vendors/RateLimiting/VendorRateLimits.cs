using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Platform.Modules.Vendors.RateLimiting;

/// <summary>
/// The vendor actions limited in the service itself, before any side effect (one singleton per host):
/// <list type="bullet">
/// <item>W-37, joining a tenant (<c>VendorJoin</c>): every join takes a permit of <c>Vendors:JoinsPerUserPerMinute</c> per
/// user before anything else; a first-time join (the company does not work with the tenant yet) also takes one of
/// <c>Vendors:JoinsPerTenantPerMinute</c> per host tenant. Only a first-time join adds a relationship, a Keycloak
/// organization membership and an audit row, and holds a pooled database connection across its Keycloak add (W-40), so
/// vendors that already work with the tenant and post the form again never use up the tenant's limit for a new one.</item>
/// <item>W-35, a consent grant (<c>ConsentLedger</c>): <c>Vendors:ConsentGrantsPerCompanyPerHour</c> per vendor company.
/// Revocations are deliberately not limited: a vendor may withdraw consent at any time (PDPL, F-64, N-02), and each grant
/// can be revoked once (<c>ux_consent_events_revokes</c>), so the ledger and audit rows stay at most twice the grant
/// limit.</item>
/// </list>
/// The limits live in the service, not in ASP.NET Core's rate-limiting middleware, because the consent page runs in a
/// Blazor circuit, where a click is not an HTTP request the middleware sees, and the service is the one path every caller
/// takes. The counts are per process (<see cref="SlidingWindowLimiter"/>): the pilot runs one web instance (W-19); with
/// several, each allows the limit until W-34 moves such limits to a shared store. A refusal is logged as a warning with
/// ids only (N-10), the first one per user, tenant or company in each window, so a client hammering a closed limit does
/// not flood the log.
/// </summary>
internal sealed partial class VendorRateLimits(IOptions<VendorsOptions> options, TimeProvider clock, ILogger<VendorRateLimits> logger)
{
    private static readonly TimeSpan JoinWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ConsentWindow = TimeSpan.FromHours(1);

    private readonly SlidingWindowLimiter _joinsPerUser = new(options.Value.JoinsPerUserPerMinute, JoinWindow, clock);
    private readonly SlidingWindowLimiter _joinsPerTenant = new(options.Value.JoinsPerTenantPerMinute, JoinWindow, clock);
    private readonly SlidingWindowLimiter _grantsPerCompany = new(options.Value.ConsentGrantsPerCompanyPerHour, ConsentWindow, clock);

    // One logged refusal per key and window: a limiter of one permit, asked only when a refusal happens.
    private readonly SlidingWindowLimiter _joinRefusalLogs = new(1, JoinWindow, clock);
    private readonly SlidingWindowLimiter _grantRefusalLogs = new(1, ConsentWindow, clock);

    /// <summary>Takes a join permit of the user's own limit; asked first, for every join.</summary>
    public bool TryJoinAsUser(Guid tenantId, string userId)
    {
        if (_joinsPerUser.TryAcquire(userId))
        {
            return true;
        }

        if (_joinRefusalLogs.TryAcquire("user:" + userId))
        {
            JoinRefused(logger, tenantId, userId, "user");
        }

        return false;
    }

    /// <summary>Takes a join permit of the host tenant's limit; asked only for a first-time join.</summary>
    public bool TryJoinTenant(Guid tenantId, string userId)
    {
        if (_joinsPerTenant.TryAcquire(tenantId.ToString("N")))
        {
            return true;
        }

        if (_joinRefusalLogs.TryAcquire("tenant:" + tenantId.ToString("N")))
        {
            JoinRefused(logger, tenantId, userId, "tenant");
        }

        return false;
    }

    /// <summary>Takes a consent grant permit of the company's limit.</summary>
    public bool TryGrantConsent(Guid companyId, string userId)
    {
        if (_grantsPerCompany.TryAcquire(companyId.ToString("N")))
        {
            return true;
        }

        if (_grantRefusalLogs.TryAcquire(companyId.ToString("N")))
        {
            GrantRefused(logger, companyId, userId);
        }

        return false;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A join of tenant {TenantId} by user {UserId} was refused by the per-{Partition} rate limit; further refusals under the same limit in the next minute are not logged.")]
    private static partial void JoinRefused(ILogger logger, Guid tenantId, string userId, string partition);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A consent grant of vendor company {CompanyId} by user {UserId} was refused by the per-company rate limit; further refusals of this company in the next hour are not logged.")]
    private static partial void GrantRefused(ILogger logger, Guid companyId, string userId);
}
