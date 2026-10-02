using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Platform.Modules.Vendors.RateLimiting;

/// <summary>
/// The vendor actions limited in the service itself, before any side effect (one singleton per host):
/// <list type="bullet">
/// <item>W-37, joining a tenant (<c>VendorJoin</c>): <c>Vendors:JoinsPerUserPerMinute</c> per user, then
/// <c>Vendors:JoinsPerTenantPerMinute</c> per host tenant. Each join adds a relationship, a Keycloak organization membership
/// and an audit row, and a first-time join holds a pooled database connection across its Keycloak add (W-40), so the
/// tenant limit also bounds how many joins can wait on a slow Keycloak at once.</item>
/// <item>W-35, a consent grant or revocation (<c>ConsentLedger</c>): <c>Vendors:ConsentChangesPerCompanyPerHour</c> per
/// vendor company, grants and revocations together, since each adds an append-only ledger row and a platform audit row.</item>
/// </list>
/// The limits live in the service, not in ASP.NET Core's rate-limiting middleware, because the consent page runs in a
/// Blazor circuit, where a click is not an HTTP request the middleware sees, and the service is the one path every caller
/// takes. The counts are per process (<see cref="SlidingWindowLimiter"/>): the pilot runs one web instance (W-19); with
/// several, each allows the limit until W-34 moves such limits to a shared store. A refusal is logged as a warning with
/// ids only (N-10).
/// </summary>
internal sealed partial class VendorRateLimits(IOptions<VendorsOptions> options, TimeProvider clock, ILogger<VendorRateLimits> logger)
{
    private readonly SlidingWindowLimiter _joinsPerUser = new(options.Value.JoinsPerUserPerMinute, TimeSpan.FromMinutes(1), clock);
    private readonly SlidingWindowLimiter _joinsPerTenant = new(options.Value.JoinsPerTenantPerMinute, TimeSpan.FromMinutes(1), clock);
    private readonly SlidingWindowLimiter _consentPerCompany = new(options.Value.ConsentChangesPerCompanyPerHour, TimeSpan.FromHours(1), clock);

    /// <summary>
    /// Takes a join permit for <paramref name="userId"/> on <paramref name="tenantId"/>. The user's limit is asked first,
    /// so a user over its own limit never uses up the tenant's for everyone else.
    /// </summary>
    public bool TryJoin(Guid tenantId, string userId)
    {
        if (!_joinsPerUser.TryAcquire(userId))
        {
            JoinRefused(logger, tenantId, userId, "user");
            return false;
        }

        if (!_joinsPerTenant.TryAcquire(tenantId.ToString("N")))
        {
            JoinRefused(logger, tenantId, userId, "tenant");
            return false;
        }

        return true;
    }

    /// <summary>Takes a consent change permit (<paramref name="change"/> is <c>grant</c> or <c>revoke</c>, for the log) for the company.</summary>
    public bool TryChangeConsent(Guid companyId, string userId, string change)
    {
        if (_consentPerCompany.TryAcquire(companyId.ToString("N")))
        {
            return true;
        }

        ConsentRefused(logger, change, companyId, userId);
        return false;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A join of tenant {TenantId} by user {UserId} was refused by the per-{Partition} rate limit.")]
    private static partial void JoinRefused(ILogger logger, Guid tenantId, string userId, string partition);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A consent {Change} of vendor company {CompanyId} by user {UserId} was refused by the per-company rate limit.")]
    private static partial void ConsentRefused(ILogger logger, string change, Guid companyId, string userId);
}
