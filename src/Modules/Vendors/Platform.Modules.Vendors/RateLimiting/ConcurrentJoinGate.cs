using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Platform.Modules.Vendors.RateLimiting;

/// <summary>
/// W-37: at most <c>Vendors:MaxConcurrentJoins</c> (10 by default) first-time joins in flight in this web instance, whatever
/// the tenant. A first-time join holds a pooled database connection from before its Keycloak add to its commit (W-40,
/// <c>JoinLock</c>) and opens a second one for its audit entry, and a failed one may run its undo after it; the rate limits
/// bound how many joins start in a minute, not how many wait on a slow Keycloak at once across tenants. With the cap at 10
/// those joins hold at most 20 of Npgsql's default 100 pooled connections (the connection strings do not change it), so
/// page loads, circuits and jobs keep the rest during a Keycloak brownout. A join that finds the gate full waits up to
/// <see cref="DefaultWait"/> for a slot and is then refused (<c>vendor.join_busy</c>) before any Keycloak call or
/// write. The refusal is logged as a warning with ids only, the first one per tenant in each minute.
/// <para>
/// <c>VendorJoin</c> takes the slot before the tenant's join permit (<see cref="VendorRateLimits.TryJoinTenant"/>), so a
/// join refused as busy does not use up the tenant's minute; the permit is an in-memory check, so the slot held meanwhile
/// holds no connection.
/// </para> Per process, like the rate limits: with several web instances each allows the cap. A singleton, disposed with
/// the host as <c>KeycloakAdminState</c> is.
/// </summary>
internal sealed partial class ConcurrentJoinGate : IDisposable
{
    internal static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _slots;
    private readonly TimeSpan _wait;
    private readonly ILogger<ConcurrentJoinGate> _logger;
    private readonly SlidingWindowLimiter _refusalLogs;

    public ConcurrentJoinGate(IOptions<VendorsOptions> options, TimeProvider clock, ILogger<ConcurrentJoinGate> logger)
        : this(options.Value.MaxConcurrentJoins, DefaultWait, clock, logger)
    {
    }

    internal ConcurrentJoinGate(int capacity, TimeSpan wait, TimeProvider clock, ILogger<ConcurrentJoinGate> logger)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _slots = new SemaphoreSlim(capacity, capacity);
        _wait = wait;
        _logger = logger;
        _refusalLogs = new SlidingWindowLimiter(1, TimeSpan.FromMinutes(1), clock);
    }

    /// <summary>The free slots now.</summary>
    internal int Available => _slots.CurrentCount;

    /// <summary>
    /// A slot for one join, released when disposed; null when none came free within the wait (logged with ids only).
    /// Cancellation propagates.
    /// </summary>
    public async Task<IDisposable?> TryEnterAsync(Guid tenantId, string userId, CancellationToken cancellationToken)
    {
        if (await _slots.WaitAsync(_wait, cancellationToken))
        {
            return new Slot(_slots);
        }

        if (_refusalLogs.TryAcquire(tenantId.ToString("N")))
        {
            Refused(_logger, tenantId, userId);
        }

        return null;
    }

    public void Dispose() => _slots.Dispose();

    [LoggerMessage(Level = LogLevel.Warning, Message = "A join of tenant {TenantId} by user {UserId} was refused: the joins in flight in this instance are at their cap; further such refusals on this tenant in the next minute are not logged.")]
    private static partial void Refused(ILogger logger, Guid tenantId, string userId);

    private sealed class Slot(SemaphoreSlim slots) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                slots.Release();
            }
        }
    }
}
