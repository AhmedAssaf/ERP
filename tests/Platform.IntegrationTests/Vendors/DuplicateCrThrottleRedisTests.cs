using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Vendors;
using Platform.Modules.Vendors.Contracts;
using Platform.Modules.Vendors.Registration;
using Platform.Shared.Caching;
using Platform.Shared.Results;
using StackExchange.Redis;

namespace Platform.IntegrationTests.Vendors;

/// <summary>
/// W-34 (pentest P-5): the duplicate-CR limit (V-6) is kept in Redis, shared by every web instance, per account and per
/// source address, both applying: five "already registered" answers per account in the window, and
/// <c>Vendors:DuplicateCrPerAddress</c> per address (three here). Each partition is one counter whose first count starts
/// its window (INCR and PEXPIRE in one script). When Redis does not answer, the call falls back to the in-process limit,
/// never to no limit, and the outage is logged once without the connection string. A limited user still reads the same
/// answer as before.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class DuplicateCrThrottleRedisTests(DatabaseFixture db, RedisFixture redis) : IClassFixture<RedisFixture>, IAsyncDisposable
{
    private const int PerAddress = 3;
    private const string DuplicateMessage = "This company already has an account on WaslaBid. Ask its administrator to add you.";
    private const string NetworkMessage = "Too many registration attempts from your network. Try again in an hour.";

    private readonly List<IAsyncDisposable> _owned = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        foreach (var owned in _owned)
        {
            await owned.DisposeAsync();
        }
    }

    [Fact]
    public async Task Two_accounts_from_the_same_address_share_the_address_limit()
    {
        var throttle = Throttle(redis.ConnectionString);
        var address = NewAddress();
        string first = NewUserId(), second = NewUserId();

        await AnswerAsync(throttle, first, address);
        await AnswerAsync(throttle, first, address);
        (await IsLimitedAsync(throttle, second, address)).ShouldBeFalse();
        await AnswerAsync(throttle, second, address);

        // Three answers from the address, two and one per account: the address is spent, neither account is.
        (await IsLimitedAsync(throttle, first, address)).ShouldBeTrue();
        (await IsLimitedAsync(throttle, second, address)).ShouldBeTrue();
        (await IsLimitedAsync(throttle, NewUserId(), address)).ShouldBeTrue("a third account from the same address");
        (await IsLimitedAsync(throttle, first, NewAddress())).ShouldBeFalse("the same account from another address");
    }

    [Fact]
    public async Task One_account_across_two_instances_shares_the_account_limit()
    {
        // Two web instances: each its own throttle and connection, one Redis.
        var one = Throttle(redis.ConnectionString, perAddress: 100);
        var two = Throttle(redis.ConnectionString, perAddress: 100);
        var user = NewUserId();

        for (var i = 0; i < DuplicateCrThrottle.Limit; i++)
        {
            await AnswerAsync(i % 2 == 0 ? one : two, user, NewAddress());
        }

        (await IsLimitedAsync(one, user, NewAddress())).ShouldBeTrue();
        (await IsLimitedAsync(two, user, NewAddress())).ShouldBeTrue();
        (await IsLimitedAsync(two, NewUserId(), NewAddress())).ShouldBeFalse();
    }

    [Fact]
    public async Task Each_counter_expires_with_its_window_from_its_first_count()
    {
        var window = TimeSpan.FromSeconds(2);
        var throttle = Throttle(redis.ConnectionString, window: window);
        var user = NewUserId();
        var address = NewAddress();
        for (var i = 0; i < PerAddress; i++)
        {
            await AnswerAsync(throttle, user, address);
        }

        (await IsLimitedAsync(throttle, user, address)).ShouldBeTrue();

        // Both keys carry the window as their TTL, set by the first count and not pushed back by later ones.
        await using var check = await redis.ConnectAsync();
        var store = check.GetDatabase();
        foreach (var key in new[] { DuplicateCrThrottle.AccountKey(user), DuplicateCrThrottle.AddressKey(address)! })
        {
            var ttl = (await store.KeyTimeToLiveAsync(key)).ShouldNotBeNull($"{key} has no expiry");
            ttl.ShouldBeGreaterThan(TimeSpan.Zero);
            ttl.ShouldBeLessThanOrEqualTo(window);
        }

        await WaitUntilAsync(async () => !await store.KeyExistsAsync(DuplicateCrThrottle.AccountKey(user)), TimeSpan.FromSeconds(10));
        (await store.KeyExistsAsync(DuplicateCrThrottle.AddressKey(address))).ShouldBeFalse();
        (await IsLimitedAsync(throttle, user, address)).ShouldBeFalse();
    }

    [Fact]
    public async Task The_keys_are_namespaced_and_carry_only_the_account_id_and_the_address()
    {
        var user = NewUserId();
        DuplicateCrThrottle.AccountKey(user).ShouldBe($"waslabid:throttle:{{dup-cr}}:account:{user}");
        DuplicateCrThrottle.AddressKey(IPAddress.Parse("203.0.113.7")).ShouldBe("waslabid:throttle:{dup-cr}:addr:203.0.113.7");
        // An IPv4 client seen over IPv6 counts as its IPv4 address; an IPv6 client by its /64, the block one subscriber gets.
        DuplicateCrThrottle.AddressKey(IPAddress.Parse("::ffff:203.0.113.7")).ShouldBe("waslabid:throttle:{dup-cr}:addr:203.0.113.7");
        DuplicateCrThrottle.AddressKey(IPAddress.Parse("2001:db8:1:2:aaaa:bbbb:cccc:dddd")).ShouldBe("waslabid:throttle:{dup-cr}:addr:2001:db8:1:2::/64");
        DuplicateCrThrottle.AddressKey(null).ShouldBeNull();
        // One hash tag: both keys of a reservation sit in one Redis Cluster slot, so the script may touch them together.
        HashSlot(DuplicateCrThrottle.AccountKey(user)).ShouldBe(HashSlot(DuplicateCrThrottle.AddressKey(IPAddress.Parse("198.51.100.1"))!));

        var throttle = Throttle(redis.ConnectionString);
        var first = IPAddress.Parse("2001:db8:77:1::1");
        var sameBlock = IPAddress.Parse("2001:db8:77:1:ffff::2");
        for (var i = 0; i < PerAddress; i++)
        {
            await AnswerAsync(throttle, NewUserId(), first);
        }

        (await IsLimitedAsync(throttle, NewUserId(), sameBlock)).ShouldBeTrue();
        await using var check = await redis.ConnectAsync();
        await check.GetDatabase().KeyDeleteAsync(DuplicateCrThrottle.AddressKey(first));
    }

    [Fact]
    public async Task Without_an_address_only_the_account_limit_applies()
    {
        var throttle = Throttle(redis.ConnectionString);
        var user = NewUserId();
        for (var i = 0; i < DuplicateCrThrottle.Limit - 1; i++)
        {
            await AnswerAsync(throttle, user, address: null);
        }

        (await IsLimitedAsync(throttle, user, address: null)).ShouldBeFalse();
        await AnswerAsync(throttle, user, address: null);
        (await IsLimitedAsync(throttle, user, address: null)).ShouldBeTrue();
    }

    [Fact]
    public async Task When_redis_is_unreachable_the_in_process_limits_apply_and_the_outage_is_logged_once()
    {
        var port = PlatformWebFactory.UnusedLoopbackPort();
        var logs = new CapturedLogs();
        var throttle = Throttle($"127.0.0.1:{port},password=redis-secret-never-logged-5e1f", logs: logs);
        var user = NewUserId();
        var address = NewAddress();

        var watch = Stopwatch.StartNew();
        for (var i = 0; i < DuplicateCrThrottle.Limit; i++)
        {
            (await IsLimitedAsync(throttle, user, NewAddress())).ShouldBeFalse();
            await AnswerAsync(throttle, user, NewAddress());
        }

        // Not unlimited: the account limit holds in memory, and so does the address limit for other accounts.
        (await IsLimitedAsync(throttle, user, NewAddress())).ShouldBeTrue();
        for (var i = 0; i < PerAddress; i++)
        {
            await AnswerAsync(throttle, NewUserId(), address);
        }

        (await IsLimitedAsync(throttle, NewUserId(), address)).ShouldBeTrue();
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20), "a refused connection must not wait for a timeout on every call");

        var warnings = logs.Entries.Where(e => e.Level >= LogLevel.Warning && e.Category == typeof(DuplicateCrThrottle).FullName).ToList();
        var warning = warnings.ShouldHaveSingleItem();
        warning.Text.ShouldContain("in-process");
        foreach (var entry in logs.Entries.Where(e => e.Category == typeof(DuplicateCrThrottle).FullName))
        {
            // N-10 and no personal data: neither the connection string nor the keys (account id, address) are logged.
            entry.Text.ShouldNotContain("redis-secret-never-logged-5e1f");
            entry.Text.ShouldNotContain(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            entry.Text.ShouldNotContain(user);
            entry.Text.ShouldNotContain(address.ToString());
        }
    }

    /// <summary>
    /// A Redis that accepted the connection and then stops answering costs a call about the lowered operation timeout
    /// (one second), not the library's five, and the call falls back to the in-process limits; a timeout the connection
    /// string sets itself is kept, which is why the second throttle waits longer.
    /// </summary>
    [Fact]
    public async Task A_redis_that_stops_answering_costs_a_call_about_a_second_and_falls_back()
    {
        await using var proxy = HangingTcpProxy.To(redis.ConnectionString);
        var logs = new CapturedLogs();
        var throttle = Throttle($"127.0.0.1:{proxy.Port}", logs: logs);
        var slow = Throttle($"127.0.0.1:{proxy.Port},asyncTimeout=3000");
        var user = NewUserId();
        await AnswerAsync(throttle, user, address: null);
        (await IsLimitedAsync(throttle, user, address: null)).ShouldBeFalse();
        (await IsLimitedAsync(slow, user, address: null)).ShouldBeFalse();
        logs.Entries.ShouldNotContain(e => e.Level >= LogLevel.Warning, "Redis answered so far");

        proxy.Hang();

        var watch = Stopwatch.StartNew();
        (await IsLimitedAsync(throttle, user, address: null)).ShouldBeFalse("the in-process count of this outage is empty");
        var lowered = watch.Elapsed;
        lowered.ShouldBeLessThan(TimeSpan.FromSeconds(2.5));
        watch.Restart();
        await IsLimitedAsync(slow, user, address: null);
        watch.Elapsed.ShouldBeGreaterThan(TimeSpan.FromSeconds(2.5), "asyncTimeout=3000 in the connection string is kept");

        // The outage is counted in memory, and logged once.
        for (var i = 0; i < DuplicateCrThrottle.Limit; i++)
        {
            await AnswerAsync(throttle, user, address: null);
        }

        (await IsLimitedAsync(throttle, user, address: null)).ShouldBeTrue();
        logs.Entries.Where(e => e.Level >= LogLevel.Warning).ShouldHaveSingleItem().Text.ShouldContain("in-process");
    }

    /// <summary>
    /// A server that accepts and never answers from the start: the first connection gives up after one attempt of the
    /// connect timeout (two seconds; with the library's three attempts it took six), then calls fail fast.
    /// </summary>
    [Fact]
    public async Task A_server_that_never_answers_does_not_hold_the_first_call_for_long()
    {
        await using var silent = HangingTcpProxy.Silent();

        var watch = Stopwatch.StartNew();
        var throttle = Throttle($"127.0.0.1:{silent.Port}");
        var user = NewUserId();
        await AnswerAsync(throttle, user, address: null);
        (await IsLimitedAsync(throttle, user, address: null)).ShouldBeFalse();

        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(4), "one connect attempt of two seconds, then fail-fast calls");
    }

    /// <summary>
    /// I-3: an address at its limit gets the network answer on both forms, the same words whatever the number, and never
    /// the claim that a company is registered: many subscribers of a mobile carrier share one address (carrier-grade NAT).
    /// </summary>
    [Fact]
    public async Task An_address_limited_caller_gets_the_network_answer_on_both_forms_and_no_claim_about_a_company()
    {
        var address = NewAddress();
        await using var host = Host(redis.ConnectionString, address);
        for (var attempt = 0; attempt < PerAddress; attempt++)
        {
            var taken = VendorRows.NewCrNumber();
            await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Beta, NewUserId(), taken, "Taken Holder", Ct);
            (await RegisterAsync(host, VendorRegistrationInputTests.Valid(taken), NewUserId())).Error.ShouldNotBeNull().Code.ShouldBe(VendorErrors.DuplicateCr);
        }

        // A fresh account from the same address, with a taken number and with a free one: the network answer both times, and
        // no company (the Keycloak Admin API is unreachable here, so going further would answer RegistrationFailed instead).
        var takenToo = VendorRows.NewCrNumber();
        await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Beta, NewUserId(), takenToo, "Taken Holder", Ct);
        var free = VendorRows.NewCrNumber();
        foreach (var cr in new[] { takenToo, free })
        {
            var limited = (await RegisterAsync(host, VendorRegistrationInputTests.Valid(cr), NewUserId())).Error.ShouldNotBeNull();
            limited.Code.ShouldBe(VendorErrors.NetworkLimited);
            limited.Message.ShouldBe(NetworkMessage);
            limited.Message.ShouldNotContain("already");
        }

        (await VendorRows.CompaniesWithCrAsync(db.OwnerConnectionString, free, Ct)).ShouldBe(0);

        // The dispute form shares the limit and gives the same answer.
        await using (var scope = host.ScopeFor(TestTenants.Acme, actingUserId: NewUserId()))
        {
            var refused = (await scope.ServiceProvider.GetRequiredService<ICrDisputes>().RaiseAsync(
                new CrDisputeRequest(free, "Ours.", VendorPrivacyNotice.CurrentVersion, VendorPrivacyNotice.English), "x@example.test", "X", Ct)).Error.ShouldNotBeNull();
            refused.Code.ShouldBe(CrDisputeErrors.NetworkLimited);
            refused.Message.ShouldBe(NetworkMessage);
        }

        // Another address is not limited by this one.
        await using var elsewhere = Host(redis.ConnectionString, NewAddress());
        (await RegisterAsync(elsewhere, VendorRegistrationInputTests.Valid(free), NewUserId())).Error.ShouldNotBeNull().Code.ShouldBe(VendorErrors.RegistrationFailed);
    }

    /// <summary>I-3: the account's limit keeps today's answers: the duplicate answer on the registration, "Limited" on the dispute form.</summary>
    [Fact]
    public async Task An_account_limited_caller_keeps_the_duplicate_answer_and_the_dispute_limit_answer()
    {
        var user = NewUserId();
        await using var host = Host(redis.ConnectionString, NewAddress(), perAddress: 100);
        for (var attempt = 0; attempt < DuplicateCrThrottle.Limit; attempt++)
        {
            var taken = VendorRows.NewCrNumber();
            await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Beta, NewUserId(), taken, "Taken Holder", Ct);
            (await RegisterAsync(host, VendorRegistrationInputTests.Valid(taken), user)).Error.ShouldNotBeNull().Code.ShouldBe(VendorErrors.DuplicateCr);
        }

        var free = VendorRows.NewCrNumber();
        var limited = (await RegisterAsync(host, VendorRegistrationInputTests.Valid(free), user)).Error.ShouldNotBeNull();
        limited.Code.ShouldBe(VendorErrors.DuplicateCr);
        limited.Message.ShouldBe(DuplicateMessage);

        await using var scope = host.ScopeFor(TestTenants.Acme, actingUserId: user);
        var refused = (await scope.ServiceProvider.GetRequiredService<ICrDisputes>().RaiseAsync(
            new CrDisputeRequest(free, "Ours.", VendorPrivacyNotice.CurrentVersion, VendorPrivacyNotice.English), "x@example.test", "X", Ct)).Error.ShouldNotBeNull();
        refused.Code.ShouldBe(CrDisputeErrors.Limited);
        refused.Message.ShouldBe("Too many commercial registration numbers were tried. Try again in an hour.");
    }

    /// <summary>
    /// I-2: the place is taken before the lookup, atomically, so ten registrations of one account sent at once with ten
    /// taken numbers get at most five true answers (each audited); the others get the same words without a lookup.
    /// </summary>
    [Fact]
    public async Task Ten_parallel_registrations_of_one_account_get_at_most_five_true_answers()
    {
        var user = NewUserId();
        await using var host = Host(redis.ConnectionString, NewAddress(), perAddress: 100);
        var taken = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            var cr = VendorRows.NewCrNumber();
            await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Beta, NewUserId(), cr, "Taken Holder", Ct);
            taken.Add(cr);
        }

        var answers = await Task.WhenAll(taken.Select(cr => RegisterAsync(host, VendorRegistrationInputTests.Valid(cr), user)));

        answers.ShouldAllBe(a => a.Error!.Code == VendorErrors.DuplicateCr);
        (await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, user, "vendor.duplicate_cr_refused", Ct)).Count.ShouldBe(DuplicateCrThrottle.Limit);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ten_parallel_reservations_of_one_account_take_five_places_with_and_without_redis(bool withRedis)
    {
        var throttle = withRedis ? Throttle(redis.ConnectionString, perAddress: 100) : MemoryThrottle(perAddress: 100);
        var user = NewUserId();

        var reservations = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => throttle.ReserveAsync(user, NewAddress(), Ct)));

        reservations.Count(r => r.Granted).ShouldBe(DuplicateCrThrottle.Limit);
        reservations.Where(r => !r.Granted).ShouldAllBe(r => r.Limit == DuplicateCrLimit.Account);
    }

    /// <summary>I-2: a number that turned out free gives its place back, so lookups of free numbers never use up the limit.</summary>
    [Fact]
    public async Task A_refunded_reservation_gives_its_place_back_and_never_below_zero()
    {
        var throttle = Throttle(redis.ConnectionString);
        var user = NewUserId();
        var address = NewAddress();
        for (var i = 0; i < 10; i++)
        {
            var reservation = await throttle.ReserveAsync(user, address, Ct);
            reservation.Granted.ShouldBeTrue();
            await throttle.RefundAsync(reservation);
        }

        await using var check = await redis.ConnectAsync();
        var store = check.GetDatabase();
        ((long)await store.StringGetAsync(DuplicateCrThrottle.AccountKey(user))).ShouldBe(0);
        ((long)await store.StringGetAsync(DuplicateCrThrottle.AddressKey(address))).ShouldBe(0);

        // A second refund of the same place does not go below zero.
        var once = await throttle.ReserveAsync(user, address, Ct);
        await throttle.RefundAsync(once);
        await throttle.RefundAsync(once);
        ((long)await store.StringGetAsync(DuplicateCrThrottle.AccountKey(user))).ShouldBe(0);
    }

    /// <summary>
    /// I-1: a Redis that answers reads but refuses writes (out of memory with noeviction, as a read-only replica or a user
    /// without scripting would) must not leave the limit unlimited: the reservation fails as a whole and is taken in memory,
    /// the memory still limits once Redis takes writes again, and the outage is logged once with the server's error code.
    /// </summary>
    [Fact]
    public async Task A_redis_that_refuses_writes_falls_back_to_memory_and_the_memory_still_limits_afterwards()
    {
        await using var admin = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString + ",allowAdmin=true");
        var server = admin.GetServers().Single();
        var logs = new CapturedLogs();
        var throttle = Throttle(redis.ConnectionString, perAddress: 100, logs: logs);
        var user = NewUserId();
        (await IsLimitedAsync(throttle, user, NewAddress())).ShouldBeFalse("Redis takes writes so far");
        try
        {
            await server.ConfigSetAsync("maxmemory-policy", "noeviction");
            await server.ConfigSetAsync("maxmemory", "1");
            (await admin.GetDatabase().StringGetAsync(DuplicateCrThrottle.AccountKey(user))).IsNull.ShouldBeFalse("reads still work");

            for (var i = 0; i < DuplicateCrThrottle.Limit; i++)
            {
                await AnswerAsync(throttle, user, NewAddress());
            }

            (await throttle.ReserveAsync(user, NewAddress(), Ct)).Limit.ShouldBe(DuplicateCrLimit.Account);
        }
        finally
        {
            await server.ConfigSetAsync("maxmemory", "0");
        }

        // Redis takes writes again; what the outage counted still limits on this instance until its window ends.
        (await throttle.ReserveAsync(user, NewAddress(), Ct)).Limit.ShouldBe(DuplicateCrLimit.Account);
        var warning = logs.Entries.Where(e => e.Level >= LogLevel.Warning).ShouldHaveSingleItem();
        warning.Text.ShouldContain("server error OOM");
        warning.Text.ShouldNotContain(user);
        warning.Text.ShouldNotContain("maxmemory");
    }

    [Fact]
    public async Task One_account_registering_through_two_hosts_is_limited_after_five_answers_in_all()
    {
        var user = NewUserId();
        await using var one = Host(redis.ConnectionString, NewAddress(), perAddress: 100);
        await using var two = Host(redis.ConnectionString, NewAddress(), perAddress: 100);
        for (var attempt = 0; attempt < DuplicateCrThrottle.Limit; attempt++)
        {
            var taken = VendorRows.NewCrNumber();
            await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Beta, NewUserId(), taken, "Taken Holder", Ct);
            (await RegisterAsync(attempt % 2 == 0 ? one : two, VendorRegistrationInputTests.Valid(taken), user)).Error.ShouldNotBeNull()
                .Code.ShouldBe(VendorErrors.DuplicateCr);
        }

        var free = VendorRows.NewCrNumber();
        foreach (var host in new[] { one, two })
        {
            var limited = (await RegisterAsync(host, VendorRegistrationInputTests.Valid(free), user)).Error.ShouldNotBeNull();
            limited.Code.ShouldBe(VendorErrors.DuplicateCr);
            limited.Message.ShouldBe(DuplicateMessage);
        }

        (await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, user, "vendor.duplicate_cr_refused", Ct)).Count.ShouldBe(DuplicateCrThrottle.Limit);
    }

    /// <summary>An answer that counts: a place taken and kept.</summary>
    private static async Task AnswerAsync(DuplicateCrThrottle throttle, string userId, IPAddress? address) =>
        (await throttle.ReserveAsync(userId, address, Ct)).Granted.ShouldBeTrue();

    /// <summary>Whether a reservation would be refused now; a place taken to find out is given back.</summary>
    private static async Task<bool> IsLimitedAsync(DuplicateCrThrottle throttle, string userId, IPAddress? address)
    {
        var reservation = await throttle.ReserveAsync(userId, address, Ct);
        await throttle.RefundAsync(reservation);
        return !reservation.Granted;
    }

    /// <summary>The Redis Cluster slot of a key: CRC16 of the hash tag between the first braces, modulo 16384.</summary>
    private static int HashSlot(string key)
    {
        var open = key.IndexOf('{', StringComparison.Ordinal);
        var close = key.IndexOf('}', open + 1);
        ushort crc = 0;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(key[(open + 1)..close]))
        {
            crc ^= (ushort)(b << 8);
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
            }
        }

        return crc % 16384;
    }

    private static DuplicateCrThrottle MemoryThrottle(int perAddress = PerAddress) =>
        new(
            Options.Create(new VendorsOptions { DuplicateCrPerAddress = perAddress }),
            TimeProvider.System,
            new HttpContextAccessor(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DuplicateCrThrottle>.Instance);

    private DuplicateCrThrottle Throttle(string connectionString, int perAddress = PerAddress, TimeSpan? window = null, CapturedLogs? logs = null)
    {
        var multiplexer = ConnectionMultiplexer.Connect(RedisConnection.Options(connectionString));
        _owned.Add(multiplexer);
        var loggers = new LoggerFactory(logs is null ? [] : [logs]);
        _owned.Add(new Disposer(loggers));
        var options = Options.Create(new VendorsOptions { DuplicateCrPerAddress = perAddress, DuplicateCrWindow = window ?? TimeSpan.FromHours(1) });
        return new DuplicateCrThrottle(options, TimeProvider.System, new HttpContextAccessor(), loggers.CreateLogger<DuplicateCrThrottle>(), multiplexer);
    }

    /// <summary>
    /// A web instance on the test database whose requests come from <paramref name="address"/>, with the throttle on the
    /// given Redis; the Keycloak Admin API is unreachable, as in <see cref="VendorRegistrationInputTests"/>.
    /// </summary>
    private ModuleHost Host(string redisConnectionString, IPAddress address, int perAddress = PerAddress) =>
        new(db.AppConnectionString, UnreachableKeycloak(), configure: services =>
        {
            services.AddRedis(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { [$"ConnectionStrings:{RedisConnection.ConnectionStringName}"] = redisConnectionString })
                .Build());
            services.Configure<VendorsOptions>(o => o.DuplicateCrPerAddress = perAddress);
            services.Replace(ServiceDescriptor.Singleton<IHttpContextAccessor>(new FixedAddress(address)));
        });

    private static async Task<Result<Guid>> RegisterAsync(ModuleHost host, VendorRegistration input, string userId)
    {
        await using var scope = host.ScopeFor(TestTenants.Acme, actingUserId: userId);
        return await scope.ServiceProvider.GetRequiredService<IVendorRegistration>().RegisterCompanyAsync(input, "applicant@example.test", Ct);
    }

    private static IConfiguration UnreachableKeycloak() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KeycloakAdmin:BaseUrl"] = $"http://127.0.0.1:{PlatformWebFactory.UnusedLoopbackPort()}",
            ["KeycloakAdmin:ClientSecret"] = "unused",
            ["KeycloakAdmin:TenantUrl"] = "https://{slug}.localhost:8443/",
        })
        .Build();

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition did not become true in time.");
            }

            await Task.Delay(200, Ct);
        }
    }

    private static string NewUserId() => Guid.NewGuid().ToString();

    /// <summary>A random address from TEST-NET-3, so tests never share an address partition.</summary>
    private static IPAddress NewAddress() =>
        new([203, 0, (byte)System.Security.Cryptography.RandomNumberGenerator.GetInt32(256), (byte)System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, 255)]);

    /// <summary>Every request of the host comes from one client address, as the forwarded-headers middleware leaves it.</summary>
    private sealed class FixedAddress(IPAddress address) : IHttpContextAccessor
    {
        public HttpContext? HttpContext
        {
            get => new DefaultHttpContext { Connection = { RemoteIpAddress = address } };
            set { }
        }
    }

    private sealed class Disposer(IDisposable inner) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            inner.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
