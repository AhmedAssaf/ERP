using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Platform.Modules.Vendors.Registration;

/// <summary>
/// Limits how often the registration (and the dispute form, which shares the limit) tells someone that a CR number is
/// already on the platform (V-6): after <see cref="Limit"/> refusals for one account, or
/// <see cref="VendorsOptions.DuplicateCrPerAddress"/> refusals from one source address, within
/// <see cref="VendorsOptions.DuplicateCrWindow"/> (an hour) of the first, that account or address is limited until the
/// window ends, and every CR number then gets the same neutral answer, so the form cannot be used to test which numbers
/// are taken. Both limits apply (W-34, pentest P-5): several accounts from one address share the address's limit.
/// <list type="bullet">
/// <item>Store: Redis (<c>ConnectionStrings:Redis</c>), shared by every web instance, one counter per partition under
/// <c>waslabid:throttle:dup-cr:account:&lt;user id&gt;</c> and <c>waslabid:throttle:dup-cr:addr:&lt;address&gt;</c>; the
/// first count sets the counter's expiry to the window, in the same script as the increment, so a counter never outlives
/// its window. Keys carry the account id (the Keycloak <c>sub</c>) and the address only, never the CR number.</item>
/// <item>Source address: the request's <c>RemoteIpAddress</c>, which the forwarded-headers middleware sets from Caddy's
/// <c>X-Forwarded-For</c> only when the request came from Caddy (W-24). An IPv4 address seen as IPv6 counts as its IPv4
/// address; an IPv6 address counts by its /64, the block one subscriber gets. Without an HTTP request (a background call)
/// only the account limit applies. Both pages that call this are statically rendered, so the request is always there.</item>
/// <item>Without Redis, or when Redis does not answer: the same limits in process memory
/// (<see cref="InProcessWindowCounter"/>) for that call, never no limit; with several instances each then allows the
/// limits. The first failure of an outage is logged as a Warning with the exception type only (never the connection
/// string or a key, N-10), and the first success after it at Information.</item>
/// </list>
/// The check and the count are separate calls, as before: two requests of one user at the same moment can both pass the
/// check, so the limit is approximate by the number of requests in flight.
/// </summary>
internal sealed partial class DuplicateCrThrottle
{
    /// <summary>Refusals per account in the window (V-6).</summary>
    internal const int Limit = 5;

    /// <summary>Keys remembered by each in-process counter.</summary>
    internal const int Capacity = 10_000;

    /// <summary>The window when <see cref="VendorsOptions.DuplicateCrWindow"/> is not set.</summary>
    internal static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private const string KeyPrefix = "waslabid:throttle:dup-cr:";

    // Each key: count one; the first count (or a counter that somehow lost its expiry) sets the window in milliseconds.
    private const string RecordScript = """
        for _, key in ipairs(KEYS) do
          local count = redis.call('INCR', key)
          if count == 1 or redis.call('PTTL', key) < 0 then
            redis.call('PEXPIRE', key, ARGV[1])
          end
        end
        return 0
        """;

    private readonly IHttpContextAccessor _http;
    private readonly ILogger<DuplicateCrThrottle> _logger;
    private readonly IConnectionMultiplexer? _redis;
    private readonly int _perAddress;
    private readonly long _windowMilliseconds;
    private readonly InProcessWindowCounter _accounts;
    private readonly InProcessWindowCounter _addresses;
    private int _redisFailing;

    public DuplicateCrThrottle(
        IOptions<VendorsOptions> options,
        TimeProvider clock,
        IHttpContextAccessor http,
        ILogger<DuplicateCrThrottle> logger,
        IConnectionMultiplexer? redis = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var settings = options.Value;
        var window = settings.DuplicateCrWindow;
        _http = http;
        _logger = logger;
        _redis = redis;
        _perAddress = settings.DuplicateCrPerAddress;
        _windowMilliseconds = (long)Math.Ceiling(window.TotalMilliseconds);
        _accounts = new InProcessWindowCounter(clock, Limit, window, Capacity);
        _addresses = new InProcessWindowCounter(clock, _perAddress, window, Capacity);
    }

    /// <summary>The Redis key of an account's counter.</summary>
    internal static string AccountKey(string userId) => $"{KeyPrefix}account:{userId}";

    /// <summary>The Redis key of a source address's counter, or null without an address.</summary>
    internal static string? AddressKey(IPAddress? address) => AddressPartition(address) is { } partition ? $"{KeyPrefix}addr:{partition}" : null;

    /// <summary>True when the acting request's account or source address already had its limit of refusals in the window.</summary>
    public Task<bool> IsLimitedAsync(string userId, CancellationToken cancellationToken) =>
        IsLimitedAsync(userId, CurrentAddress(), cancellationToken);

    /// <summary>Counts one duplicate refusal for the acting request's account and source address.</summary>
    public Task RecordAsync(string userId, CancellationToken cancellationToken) =>
        RecordAsync(userId, CurrentAddress(), cancellationToken);

    internal async Task<bool> IsLimitedAsync(string userId, IPAddress? address, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var accountKey = AccountKey(userId);
        var addressKey = AddressKey(address);
        if (_redis is not null)
        {
            try
            {
                RedisKey[] keys = addressKey is null ? [accountKey] : [accountKey, addressKey];
                var counts = await _redis.GetDatabase().StringGetAsync(keys).WaitAsync(cancellationToken);
                RedisAnswered();
                return Count(counts[0]) >= Limit || (counts.Length > 1 && Count(counts[1]) >= _perAddress);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                RedisFailed(ex);
            }
        }

        return _accounts.IsLimited(accountKey) || (addressKey is not null && _addresses.IsLimited(addressKey));
    }

    internal async Task RecordAsync(string userId, IPAddress? address, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var accountKey = AccountKey(userId);
        var addressKey = AddressKey(address);
        if (_redis is not null)
        {
            try
            {
                RedisKey[] keys = addressKey is null ? [accountKey] : [accountKey, addressKey];
                await _redis.GetDatabase().ScriptEvaluateAsync(RecordScript, keys, [_windowMilliseconds]).WaitAsync(cancellationToken);
                RedisAnswered();
                return;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                RedisFailed(ex);
            }
        }

        _accounts.Record(accountKey);
        if (addressKey is not null)
        {
            _addresses.Record(addressKey);
        }
    }

    /// <summary>
    /// The partition of a source address: an IPv4 address as written, an IPv4-mapped IPv6 address as its IPv4 address,
    /// any other IPv6 address as its /64 network.
    /// </summary>
    private static string? AddressPartition(IPAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return $"{new IPAddress(bytes)}/64";
    }

    private static long Count(RedisValue value) => value.TryParse(out long count) ? count : 0;

    private IPAddress? CurrentAddress() => _http.HttpContext?.Connection.RemoteIpAddress;

    private void RedisAnswered()
    {
        if (Interlocked.Exchange(ref _redisFailing, 0) == 1)
        {
            RedisBack(_logger);
        }
    }

    /// <summary>
    /// Any failure of a Redis call that the caller did not cancel counts as Redis not answering: besides
    /// <see cref="RedisException"/> and <see cref="TimeoutException"/>, a connection the client aborts after repeated
    /// timeouts faults its pending commands with an <see cref="IOException"/>. Registration must keep working, so every
    /// such failure falls back, and it is logged (once per outage).
    /// </summary>
    private void RedisFailed(Exception exception)
    {
        if (Interlocked.Exchange(ref _redisFailing, 1) == 0)
        {
            // The exception's message can name the endpoint and the keys (an account id, an address); only its type is logged.
            RedisDown(_logger, exception.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis did not answer the duplicate-CR throttle ({ErrorType}); the in-process limits apply on this instance until it answers again.")]
    private static partial void RedisDown(ILogger logger, string errorType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Redis answers the duplicate-CR throttle again; the shared limits apply.")]
    private static partial void RedisBack(ILogger logger);
}
