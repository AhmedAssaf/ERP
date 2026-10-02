using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Platform.Modules.Vendors.Registration;

/// <summary>Which duplicate-CR limit refused a reservation.</summary>
internal enum DuplicateCrLimit
{
    /// <summary>Not limited: the reservation was taken.</summary>
    None = 0,

    /// <summary>The account had its five answers in the window; it gets the duplicate answer for every CR number (V-6).</summary>
    Account = 1,

    /// <summary>
    /// The source address had its answers in the window; the caller gets the network answer, which names no CR number,
    /// so a subscriber behind a shared address (carrier-grade NAT) is never told their own company is registered.
    /// </summary>
    Address = 2,
}

/// <summary>
/// A place in the duplicate-CR limits taken before a CR number is looked up (W-34, I-2): kept when the caller tells
/// someone the number is taken (or, on the dispute form, that no company has it), given back otherwise
/// (<see cref="DuplicateCrThrottle.RefundAsync"/>).
/// </summary>
internal sealed record DuplicateCrReservation(DuplicateCrLimit Limit, string AccountKey, string? AddressKey, bool InRedis)
{
    public bool Granted => Limit == DuplicateCrLimit.None;
}

/// <summary>
/// Limits how often the registration (and the dispute form, which shares the limits) answers whether a CR number is on
/// the platform (V-6): <see cref="Limit"/> answers per account and <see cref="VendorsOptions.DuplicateCrPerAddress"/> per
/// source address within <see cref="VendorsOptions.DuplicateCrWindow"/> (an hour) of the first, both applying (W-34,
/// pentest P-5). An account at its limit gets the duplicate answer for every number, as before; an address at its limit
/// gets an answer of its own that names no number (<see cref="DuplicateCrLimit.Address"/>).
/// <list type="bullet">
/// <item>Reserve, then refund (I-2): a caller takes a place in both counters before the lookup, atomically and only while
/// both are below their limits, and gives it back when the lookup gave no answer to count; so requests sent in parallel
/// cannot all pass at a count of zero, and at most the limit of answers is ever given.</item>
/// <item>Store: Redis (<c>ConnectionStrings:Redis</c>), shared by every web instance, one counter per partition under
/// <c>waslabid:throttle:{dup-cr}:account:&lt;user id&gt;</c> and <c>waslabid:throttle:{dup-cr}:addr:&lt;address&gt;</c> (the
/// hash tag keeps both in one Redis Cluster slot for the script). The reservation is one Lua script that reads, increments
/// and, on a counter's first count, sets its expiry to the window. Keys carry the account id and the address only, never
/// the CR number.</item>
/// <item>Source address: the request's <c>RemoteIpAddress</c>, which the forwarded-headers middleware sets from Caddy's
/// <c>X-Forwarded-For</c> only when the request came from Caddy (W-24). An IPv4 address seen as IPv6 counts as its IPv4
/// address; an IPv6 address counts by its /64. Without an HTTP request only the account limit applies.</item>
/// <item>When Redis does not run the script (down, timed out, or refusing writes: OOM, READONLY, NOPERM), the reservation
/// is taken in process memory (<see cref="InProcessWindowCounter"/>), never skipped. The memory is also read on every
/// reservation, so what an outage counted still limits once Redis answers again (I-1). The first failure of an outage is
/// logged once as a Warning with the exception type and the server's error code only (never the message, a key or the
/// connection string, N-10), the first success after it at Information.</item>
/// </list>
/// </summary>
internal sealed partial class DuplicateCrThrottle
{
    /// <summary>Answers per account in the window (V-6).</summary>
    internal const int Limit = 5;

    /// <summary>Keys remembered by each in-process counter.</summary>
    internal const int Capacity = 10_000;

    /// <summary>The window when <see cref="VendorsOptions.DuplicateCrWindow"/> is not set.</summary>
    internal static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private const string KeyPrefix = "waslabid:throttle:{dup-cr}:";

    // KEYS[1] the account, KEYS[2] the address (optional); ARGV[1] the window in ms, ARGV[2] and ARGV[3] the limits.
    // Returns 0 when the place was taken in every counter, 1 when the account is at its limit, 2 when the address is.
    private const string ReserveScript = """
        if tonumber(redis.call('GET', KEYS[1]) or '0') >= tonumber(ARGV[2]) then
          return 1
        end
        if KEYS[2] and tonumber(redis.call('GET', KEYS[2]) or '0') >= tonumber(ARGV[3]) then
          return 2
        end
        for _, key in ipairs(KEYS) do
          local count = redis.call('INCR', key)
          if count == 1 or redis.call('PTTL', key) < 0 then
            redis.call('PEXPIRE', key, ARGV[1])
          end
        end
        return 0
        """;

    // Gives one place back in each counter, never below zero; a counter that expired meanwhile is left alone.
    private const string RefundScript = """
        for _, key in ipairs(KEYS) do
          if tonumber(redis.call('GET', key) or '0') > 0 then
            redis.call('DECR', key)
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
    private readonly Lock _memory = new();
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

    /// <summary>Takes a place for the acting request's account and source address, or says which limit refused it.</summary>
    public Task<DuplicateCrReservation> ReserveAsync(string userId, CancellationToken cancellationToken) =>
        ReserveAsync(userId, CurrentAddress(), cancellationToken);

    internal async Task<DuplicateCrReservation> ReserveAsync(string userId, IPAddress? address, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var accountKey = AccountKey(userId);
        var addressKey = AddressKey(address);

        // What an outage counted in memory still applies while its window lasts (I-1).
        if (MemoryLimit(accountKey, addressKey) is var counted and not DuplicateCrLimit.None)
        {
            return new DuplicateCrReservation(counted, accountKey, addressKey, InRedis: false);
        }

        if (_redis is not null)
        {
            try
            {
                RedisKey[] keys = addressKey is null ? [accountKey] : [accountKey, addressKey];
                var result = (int)await _redis.GetDatabase()
                    .ScriptEvaluateAsync(ReserveScript, keys, [_windowMilliseconds, Limit, _perAddress])
                    .WaitAsync(cancellationToken);
                RedisAnswered();
                return new DuplicateCrReservation((DuplicateCrLimit)result, accountKey, addressKey, InRedis: true);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                RedisFailed(ex);
            }
        }

        return new DuplicateCrReservation(ReserveInMemory(accountKey, addressKey), accountKey, addressKey, InRedis: false);
    }

    /// <summary>
    /// Gives back a granted reservation that did not end in a counted answer. Not cancellable: a request that ends early
    /// still gives its place back. A refund Redis does not take is logged and dropped, so the place stays used until the
    /// window ends (the safe side).
    /// </summary>
    public async Task RefundAsync(DuplicateCrReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        if (!reservation.Granted)
        {
            return;
        }

        if (!reservation.InRedis)
        {
            lock (_memory)
            {
                _accounts.Release(reservation.AccountKey);
                if (reservation.AddressKey is not null)
                {
                    _addresses.Release(reservation.AddressKey);
                }
            }

            return;
        }

        try
        {
            RedisKey[] keys = reservation.AddressKey is null ? [reservation.AccountKey] : [reservation.AccountKey, reservation.AddressKey];
            await _redis!.GetDatabase().ScriptEvaluateAsync(RefundScript, keys);
            RedisAnswered();
        }
        catch (Exception ex)
        {
            RedisFailed(ex);
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

    /// <summary>
    /// The error code a Redis server put first in its reply (<c>OOM</c>, <c>READONLY</c>, <c>NOPERM</c>, <c>ERR</c>, ...), or
    /// null. Only an upper-case word is taken: the rest of a server message can name a user, a key or a script.
    /// </summary>
    internal static string? ServerErrorCode(Exception exception) =>
        exception is RedisServerException && ErrorCode().Match(exception.Message) is { Success: true } match ? match.Value : null;

    [GeneratedRegex("^[A-Z]{2,16}(?=\\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorCode();

    private DuplicateCrLimit MemoryLimit(string accountKey, string? addressKey)
    {
        lock (_memory)
        {
            return _accounts.IsLimited(accountKey) ? DuplicateCrLimit.Account
                : addressKey is not null && _addresses.IsLimited(addressKey) ? DuplicateCrLimit.Address
                : DuplicateCrLimit.None;
        }
    }

    private DuplicateCrLimit ReserveInMemory(string accountKey, string? addressKey)
    {
        lock (_memory)
        {
            if (_accounts.IsLimited(accountKey))
            {
                return DuplicateCrLimit.Account;
            }

            if (addressKey is not null && _addresses.IsLimited(addressKey))
            {
                return DuplicateCrLimit.Address;
            }

            _accounts.Record(accountKey);
            if (addressKey is not null)
            {
                _addresses.Record(addressKey);
            }

            return DuplicateCrLimit.None;
        }
    }

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
    /// such failure falls back, and it is logged once per outage.
    /// </summary>
    private void RedisFailed(Exception exception)
    {
        if (Interlocked.Exchange(ref _redisFailing, 1) == 0)
        {
            // The exception's message can name the endpoint, a user and the keys (an account id, an address); only its type
            // and the server's error code are logged.
            RedisDown(_logger, exception.GetType().Name, ServerErrorCode(exception) ?? "none");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis did not take the duplicate-CR throttle's call ({ErrorType}, server error {ServerError}); the in-process limits apply on this instance until it answers again.")]
    private static partial void RedisDown(ILogger logger, string errorType, string serverError);

    [LoggerMessage(Level = LogLevel.Information, Message = "Redis answers the duplicate-CR throttle again; the shared limits apply.")]
    private static partial void RedisBack(ILogger logger);
}
