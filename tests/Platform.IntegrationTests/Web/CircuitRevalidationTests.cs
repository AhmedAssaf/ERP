using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.IntegrationTests.Identity;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Vendors;
using Platform.Modules.Vendors.Contracts;
using Platform.Shared.Tenancy;
using Platform.Web.Account;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// W-21 in an open Blazor circuit: the circuit's authentication state is revalidated every minute through the same
/// membership check as HTTP requests; when it fails, or the connection cookie's expiry passes, the circuit's user becomes
/// anonymous, the browser is sent to a full reload of the page (whose request is challenged, so it shows the sign-in
/// page), and no further event of that circuit runs, even from a client that ignores the reload. A circuit on a tenant host
/// is revalidated against the tenant its circuit handler set; one without a tenant off the platform host fails closed.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class CircuitRevalidationTests(DatabaseFixture db)
{
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(20);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_circuit_whose_member_was_removed_turns_anonymous_and_reloads_into_the_sign_in_page()
    {
        var navigation = new RecordingNavigationManager("https://acme.localhost/", "https://acme.localhost/admin/staff");
        var guard = Guard(navigation);
        var context = new QueueSynchronizationContext();
        await OpenOnAsync(context, guard);
        var revalidation = new ScriptedRevalidation(stillMember: false);
        using var provider = Provider(revalidation, guard, TestTenants.Acme);

        var state = await SignInAndWaitForSignOutAsync(provider);
        context.RunPending();

        state.User.Identity?.IsAuthenticated.ShouldNotBe(true);
        revalidation.SignedInAt.ShouldBe([null], "a circuit knows no sign-in time; the HTTP request that opened it recorded one");
        navigation.Navigations.ShouldBe([("https://acme.localhost/admin/staff", true)]);
        context.PostedFromThreadPool.ShouldBeTrue("the navigation runs on the circuit's own synchronization context");
    }

    [Fact]
    public async Task A_circuit_whose_member_is_still_in_the_organization_stays_signed_in()
    {
        var navigation = new RecordingNavigationManager("https://acme.localhost/", "https://acme.localhost/vendor");
        var guard = Guard(navigation);
        var revalidation = new ScriptedRevalidation(stillMember: true);
        using var provider = Provider(revalidation, guard, TestTenants.Acme);

        provider.SetAuthenticationState(Task.FromResult(new AuthenticationState(Member())));
        await revalidation.CalledTwice.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        (await provider.GetAuthenticationStateAsync()).User.Identity?.IsAuthenticated.ShouldBe(true);
        guard.Ended.ShouldBeFalse();
        navigation.Navigations.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_squatters_open_circuit_ends_once_the_company_moved_to_the_claimant()
    {
        // W-33 review: an uphold whose Keycloak step taking the squatter out of the organization failed leaves the squatter
        // a member, so the membership check keeps passing, and the circuit's vendor context was set once when it opened.
        // The revalidation also asks the database whose company it is now, past what the circuit's scope remembers.
        var squatter = Guid.NewGuid().ToString();
        var crNumber = VendorRows.NewCrNumber();
        var companyId = await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Acme, squatter, crNumber, "Circuit Squatter Co", Ct);
        await VendorDocumentRows.InsertAsync(db.OwnerConnectionString, companyId, VendorDocumentTypes.CrCertificate, new DateOnly(2031, 1, 1), "clean", isCurrent: true, Ct);
        await using var host = new ModuleHost(db.AppConnectionString, configure: services =>
            services.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => new FakeVendorAccounts { FailRevoke = true })));
        await using var circuit = host.ScopeFor(TestTenants.Acme, vendorCompanyId: companyId, actingUserId: squatter);
        var users = circuit.ServiceProvider.GetRequiredService<IVendorUsers>();
        (await users.FindCompanyAsync(squatter, Ct)).ShouldBe(companyId, "the circuit's scope now remembers the company");
        var navigation = new RecordingNavigationManager("https://acme.localhost/", "https://acme.localhost/vendor");
        var guard = Guard(navigation);
        var revalidation = new ScriptedRevalidation(stillMember: true);
        using var provider = Provider(
            revalidation, guard, TestTenants.Acme, vendors: circuit.ServiceProvider.GetRequiredService<IVendorAccessor>(), users: users);

        provider.SetAuthenticationState(Task.FromResult(new AuthenticationState(VendorUser(squatter))));
        await revalidation.CalledTwice.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        guard.Ended.ShouldBeFalse("the squatter still holds the company");

        var signedOut = WaitForSignOutAsync(provider);
        var claimant = Guid.NewGuid().ToString();
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        Guid disputeId;
        await using (var scope = host.ScopeFor(TestTenants.Acme, actingUserId: claimant))
        {
            disputeId = (await scope.ServiceProvider.GetRequiredService<ICrDisputes>().RaiseAsync(
                new CrDisputeRequest(crNumber, "We own this company.", VendorPrivacyNotice.CurrentVersion, VendorPrivacyNotice.English),
                $"{claimant}@example.test", "Claimant", Ct)).Value;
        }

        await using (var scope = host.PlatformScope(admin))
        {
            (await scope.ServiceProvider.GetRequiredService<ICrOwnershipAdministration>().UpholdAsync(disputeId, "Checked.", admin, Ct))
                .Value.IdentityProviderUpdated.ShouldBeFalse("the squatter's organization membership was not taken back");
        }

        var state = await signedOut;

        state.User.Identity?.IsAuthenticated.ShouldNotBe(true);
        guard.Ended.ShouldBeTrue();
    }

    [Fact]
    public async Task Asking_for_the_current_company_leaves_what_the_circuit_remembers_untouched()
    {
        // Second review, m-2: the revalidation loop asks off the render thread, while the circuit's pages read the scope's
        // answers during render; the answers are a plain dictionary, so the loop must not write to it.
        var userId = Guid.NewGuid().ToString();
        var companyId = await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Acme, userId, VendorRows.NewCrNumber(), "Circuit Cache Co", Ct);
        await using var host = new ModuleHost(db.AppConnectionString);
        await using var circuit = host.ScopeFor(TestTenants.Acme, vendorCompanyId: companyId, actingUserId: userId);
        var users = circuit.ServiceProvider.GetRequiredService<IVendorUsers>();
        (await users.FindCompanyAsync(userId, Ct)).ShouldBe(companyId);

        await using (var owner = new Npgsql.NpgsqlConnection(db.OwnerConnectionString))
        {
            await owner.OpenAsync(Ct);
            await using var delete = new Npgsql.NpgsqlCommand("delete from vendor.vendor_users where user_id = @user", owner);
            delete.Parameters.AddWithValue("user", userId);
            (await delete.ExecuteNonQueryAsync(Ct)).ShouldBe(1);
        }

        (await users.FindCurrentCompanyAsync(userId, Ct)).ShouldBeNull("the database is asked");
        (await users.FindCompanyAsync(userId, Ct)).ShouldBe(companyId, "the scope's answers were not written by the check");
    }

    [Fact]
    public async Task A_circuit_ends_once_its_connection_cookie_has_expired()
    {
        // Review: a circuit must not outlive the cookie that opened it. The cookie expires in 30 minutes; the membership
        // still stands, so only the expiry can end it.
        var clock = new TestClock();
        var navigation = new RecordingNavigationManager("https://acme.localhost/", "https://acme.localhost/admin/staff");
        var guard = Guard(navigation, Connection(expiresUtc: clock.GetUtcNow().AddMinutes(30)));
        var context = new QueueSynchronizationContext();
        await OpenOnAsync(context, guard);
        var revalidation = new ScriptedRevalidation(stillMember: true);
        using var provider = Provider(revalidation, guard, TestTenants.Acme, clock: clock);

        provider.SetAuthenticationState(Task.FromResult(new AuthenticationState(Member())));
        await revalidation.CalledTwice.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        guard.Ended.ShouldBeFalse("the cookie has not expired yet");
        // The ticket keeps its expiry to the second.
        guard.SessionExpiresAt.ShouldNotBeNull().ShouldBe(clock.GetUtcNow().AddMinutes(30), TimeSpan.FromSeconds(1));

        clock.Advance(TimeSpan.FromMinutes(30));
        var state = await WaitForSignOutAsync(provider);
        context.RunPending();

        state.User.Identity?.IsAuthenticated.ShouldNotBe(true);
        guard.Ended.ShouldBeTrue();
        navigation.Navigations.ShouldBe([("https://acme.localhost/admin/staff", true)]);
    }

    [Fact]
    public async Task A_circuit_without_a_tenant_off_the_platform_host_fails_closed()
    {
        // Review: with no tenant the membership check has nothing to compare and answers true, so a circuit on a tenant
        // host that somehow lost its tenant would never be revalidated. The provider refuses it instead.
        var navigation = new RecordingNavigationManager("https://acme.localhost/", "https://acme.localhost/vendor");
        var guard = Guard(navigation);
        var revalidation = new ScriptedRevalidation(stillMember: true);
        using var provider = Provider(revalidation, guard, tenant: null);

        var state = await SignInAndWaitForSignOutAsync(provider);

        state.User.Identity?.IsAuthenticated.ShouldNotBe(true);
        guard.Ended.ShouldBeTrue();
    }

    [Fact]
    public async Task A_platform_circuit_has_no_tenant_and_stays_signed_in()
    {
        var navigation = new RecordingNavigationManager("https://platform.localhost/", "https://platform.localhost/platform");
        var guard = Guard(navigation);
        var revalidation = new ScriptedRevalidation(stillMember: true);
        using var provider = Provider(revalidation, guard, tenant: null, platform: true);

        provider.SetAuthenticationState(Task.FromResult(new AuthenticationState(Member())));
        await revalidation.CalledTwice.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        guard.Ended.ShouldBeFalse();
    }

    [Fact]
    public async Task An_ended_circuit_reloads_once_however_many_inbound_activities_follow_and_runs_none_of_them()
    {
        // QA D1: the browser's reply to the reload's JavaScript call is itself inbound activity; answering every inbound
        // activity with another reload made a real tab reload thousands of times. One reload per circuit, then refuse.
        var navigation = new RecordingNavigationManager("https://acme.localhost/", "https://acme.localhost/vendor/consent");
        var guard = Guard(navigation);
        var context = new QueueSynchronizationContext();
        await OpenOnAsync(context, guard);
        var runs = 0;
        var inbound = guard.CreateInboundActivityHandler(_ =>
        {
            runs++;
            return Task.CompletedTask;
        });

        await inbound(null!);
        guard.End();
        guard.End();
        for (var i = 0; i < 50; i++)
        {
            await inbound(null!);
            context.RunPending();
        }

        runs.ShouldBe(1, "only the activity before the end ran");
        navigation.Navigations.ShouldBe([("https://acme.localhost/vendor/consent", true)]);
    }

    [Fact]
    public async Task An_ended_circuit_still_connected_after_the_reload_is_disconnected()
    {
        // A client that ignores the reload keeps its connection; the guard drops it instead of reloading again.
        var connection = new DefaultHttpContext();
        var lifetime = new RecordingLifetime();
        connection.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        var navigation = new RecordingNavigationManager("https://acme.localhost/", "https://acme.localhost/admin/staff");
        var guard = new CircuitSessionGuard(
            navigation, new HttpContextAccessor { HttpContext = connection }, NullLogger<CircuitSessionGuard>.Instance, TimeSpan.FromMilliseconds(50));
        await OpenOnAsync(new QueueSynchronizationContext(), guard);
        await guard.OnConnectionUpAsync(null!, Ct);

        guard.End();

        await lifetime.Aborted.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        CircuitSessionGuard.DisconnectAfter.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task An_ended_circuit_whose_browser_left_after_the_reload_is_not_disconnected()
    {
        var connection = new DefaultHttpContext();
        var lifetime = new RecordingLifetime();
        connection.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        var navigation = new RecordingNavigationManager("https://acme.localhost/", "https://acme.localhost/admin/staff");
        var guard = new CircuitSessionGuard(
            navigation, new HttpContextAccessor { HttpContext = connection }, NullLogger<CircuitSessionGuard>.Instance, TimeSpan.FromMilliseconds(200));
        await OpenOnAsync(new QueueSynchronizationContext(), guard);
        await guard.OnConnectionUpAsync(null!, Ct);

        guard.End();
        await guard.OnConnectionDownAsync(null!, Ct);
        await Task.Delay(TimeSpan.FromMilliseconds(600), Ct);

        lifetime.Aborted.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task An_ended_circuit_that_reconnects_gets_exactly_one_new_reload_and_is_disconnected_if_it_stays()
    {
        // Final check: a laptop sleeps, the user is removed, the reload goes to a client that is not there. When the circuit
        // reconnects within the retention window, that connection gets one reload of its own (not one per activity), and is
        // dropped if it stays; the first connection, long gone, is never touched.
        var first = Connection(out var firstLifetime);
        var accessor = new HttpContextAccessor { HttpContext = first };
        var navigation = new RecordingNavigationManager("https://acme.localhost/", "https://acme.localhost/admin/staff");
        var guard = new CircuitSessionGuard(navigation, accessor, NullLogger<CircuitSessionGuard>.Instance, TimeSpan.FromMilliseconds(100));
        var context = new QueueSynchronizationContext();
        await OpenOnAsync(context, guard);
        await guard.OnConnectionUpAsync(null!, Ct);
        await guard.OnConnectionDownAsync(null!, Ct);

        guard.End();
        context.RunPending();
        await Task.Delay(TimeSpan.FromMilliseconds(400), Ct);
        firstLifetime.Aborted.IsCompleted.ShouldBeFalse("a connection that is already down is not aborted");

        var second = Connection(out var secondLifetime);
        accessor.HttpContext = second;
        await guard.OnConnectionUpAsync(null!, Ct);
        var inbound = guard.CreateInboundActivityHandler(_ => throw new InvalidOperationException("an ended circuit runs nothing"));
        for (var i = 0; i < 50; i++)
        {
            await inbound(null!);
            context.RunPending();
        }

        navigation.Navigations.ShouldBe([("https://acme.localhost/admin/staff", true), ("https://acme.localhost/admin/staff", true)],
            "one reload for the lost connection, one for the new one, none per activity");
        await secondLifetime.Aborted.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        firstLifetime.Aborted.IsCompleted.ShouldBeFalse("the abort targets the connection that is up, not the first one");
    }

    [Fact]
    public async Task An_abort_that_fails_on_a_disposed_connection_is_logged_not_thrown()
    {
        var connection = new DefaultHttpContext();
        connection.Features.Set<IHttpRequestLifetimeFeature>(new DisposedLifetime());
        var logger = new CountingLogger<CircuitSessionGuard>();
        var guard = new CircuitSessionGuard(
            new RecordingNavigationManager("https://acme.localhost/", "https://acme.localhost/admin/staff"),
            new HttpContextAccessor { HttpContext = connection }, logger, TimeSpan.FromMilliseconds(50));
        await OpenOnAsync(new QueueSynchronizationContext(), guard);
        await guard.OnConnectionUpAsync(null!, Ct);

        guard.End();

        await logger.Warned.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        logger.Messages.ShouldContain(m => m.Contains("could not be aborted", StringComparison.Ordinal));
    }

    [Fact]
    public void The_host_revalidates_circuits_every_minute_so_a_removal_ends_a_circuit_within_five_minutes()
    {
        // Keycloak up: the confirmation just before the removal is reused for MemberFor, and the circuit's next
        // revalidation comes at most one interval later, plus the time of that check.
        MembershipRevalidatingStateProvider.Interval.ShouldBe(TimeSpan.FromMinutes(1));
        (MembershipRevalidator.MemberFor + MembershipRevalidatingStateProvider.Interval + MembershipRevalidator.CheckTimeout)
            .ShouldBeLessThan(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void A_removal_during_a_keycloak_failure_still_ends_a_circuit_within_five_minutes()
    {
        // Review: Keycloak failing after the removal keeps the session for the grace after the last confirmation, and an
        // open circuit notices one interval (and one check timeout) later at most.
        (MembershipRevalidator.Grace + MembershipRevalidatingStateProvider.Interval + MembershipRevalidator.CheckTimeout)
            .ShouldBeLessThan(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task The_web_host_uses_the_revalidating_provider_for_circuits_on_every_host()
    {
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        await using var scope = factory.Services.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<AuthenticationStateProvider>().ShouldBeOfType<MembershipRevalidatingStateProvider>();
        scope.ServiceProvider.GetServices<CircuitHandler>().ShouldContain(h => h is CircuitSessionGuard);
        scope.ServiceProvider.GetRequiredService<CircuitSessionGuard>()
            .ShouldBeSameAs(scope.ServiceProvider.GetServices<CircuitHandler>().OfType<CircuitSessionGuard>().Single());
    }

    [Fact]
    public async Task The_circuit_scope_revalidates_against_the_tenant_its_circuit_handler_set()
    {
        // Review: the circuit's revalidation runs in the circuit's scope; it must see the tenant TenantCircuitHandler set
        // when the circuit opened, or it would answer true for everyone.
        var source = new NotMemberSource();
        await using var factory = new PlatformWebFactory(db.AppConnectionString).WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Singleton<IOrganizationMembershipSource>(source))));
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var connection = new DefaultHttpContext { RequestServices = services };
        connection.Request.Scheme = "https";
        connection.Request.Host = new HostString("acme.localhost");
        services.GetRequiredService<IHttpContextAccessor>().HttpContext = connection;
        ((IHostEnvironmentNavigationManager)services.GetRequiredService<NavigationManager>())
            .Initialize("https://acme.localhost/", "https://acme.localhost/admin/staff");

        foreach (var handler in services.GetServices<CircuitHandler>().OrderBy(h => h.Order))
        {
            await handler.OnCircuitOpenedAsync(null!, Ct);
        }

        var subject = $"w21.circuit.{Guid.NewGuid():N}";
        var stillMember = await services.GetRequiredService<IMembershipRevalidation>().IsStillMemberAsync(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", subject), new Claim("organization", "acme")], "test")), null, Ct);

        services.GetRequiredService<ITenantAccessor>().Current.ShouldBe(TestTenants.Acme);
        stillMember.ShouldBeFalse();
        source.Asked.ShouldBe([("acme", subject)]);
    }

    private static ClaimsPrincipal Member() =>
        new(new ClaimsIdentity([new Claim("sub", "acme.member"), new Claim("organization", "acme")], "test"));

    private static ClaimsPrincipal VendorUser(string userId) =>
        new(new ClaimsIdentity([new Claim("sub", userId), new Claim("organization", "acme"), new Claim("roles", "vendor")], "test"));

    private static CircuitSessionGuard Guard(NavigationManager navigation, HttpContext? connection = null) =>
        new(navigation, new HttpContextAccessor { HttpContext = connection }, NullLogger<CircuitSessionGuard>.Instance);

    private static MembershipRevalidatingStateProvider Provider(
        IMembershipRevalidation revalidation,
        CircuitSessionGuard guard,
        TenantContext? tenant,
        bool platform = false,
        TimeProvider? clock = null,
        IVendorAccessor? vendors = null,
        IVendorUsers? users = null)
    {
        var tenants = new TenantAccessor();
        if (tenant is not null)
        {
            tenants.Set(tenant);
        }

        var platformContext = new PlatformRequestContext();
        if (platform)
        {
            platformContext.MarkPlatform();
        }

        return new MembershipRevalidatingStateProvider(
            NullLoggerFactory.Instance, revalidation, guard, tenants, platformContext, vendors ?? new VendorAccessor(), users ?? new NoVendorUsers(),
            clock ?? TimeProvider.System, Fast);
    }

    /// <summary>The /_blazor connection request as the authentication middleware leaves it: the cookie ticket's properties.</summary>
    private static DefaultHttpContext Connection(DateTimeOffset expiresUtc)
    {
        var connection = new DefaultHttpContext();
        var ticket = new AuthenticationTicket(Member(), new AuthenticationProperties { ExpiresUtc = expiresUtc }, "Cookies");
        connection.Features.Set<IAuthenticateResultFeature>(new ResultFeature(AuthenticateResult.Success(ticket)));
        return connection;
    }

    private static async Task<AuthenticationState> SignInAndWaitForSignOutAsync(MembershipRevalidatingStateProvider provider)
    {
        var signedOut = WaitForSignOutAsync(provider);
        provider.SetAuthenticationState(Task.FromResult(new AuthenticationState(Member())));
        return await signedOut;
    }

    private static Task<AuthenticationState> WaitForSignOutAsync(MembershipRevalidatingStateProvider provider)
    {
        var signedOut = new TaskCompletionSource<AuthenticationState>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.AuthenticationStateChanged += async task =>
        {
            var changed = await task;
            if (changed.User.Identity?.IsAuthenticated != true)
            {
                signedOut.TrySetResult(changed);
            }
        };
        return signedOut.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    // The circuit opens on its own synchronization context (the renderer's); the guard remembers it there.
    private static async Task OpenOnAsync(SynchronizationContext context, CircuitSessionGuard guard)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            await guard.OnCircuitOpenedAsync(null!, Ct);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static DefaultHttpContext Connection(out RecordingLifetime lifetime)
    {
        var connection = new DefaultHttpContext();
        lifetime = new RecordingLifetime();
        connection.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        return connection;
    }

    private sealed class DisposedLifetime : IHttpRequestLifetimeFeature
    {
        public CancellationToken RequestAborted { get; set; }

        public void Abort() => throw new ObjectDisposedException("HttpContext");
    }

    private sealed class CountingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        private readonly TaskCompletionSource _warned = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Messages { get; } = [];

        public Task Warned => _warned.Task;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Messages)
            {
                Messages.Add(formatter(state, exception));
            }

            if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning && formatter(state, exception).Contains("could not be aborted", StringComparison.Ordinal))
            {
                _warned.TrySetResult();
            }
        }
    }

    private sealed class RecordingLifetime : IHttpRequestLifetimeFeature
    {
        private readonly TaskCompletionSource _aborted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Aborted => _aborted.Task;

        public CancellationToken RequestAborted { get; set; }

        public void Abort() => _aborted.TrySetResult();
    }

    private sealed class ResultFeature(AuthenticateResult result) : IAuthenticateResultFeature
    {
        public AuthenticateResult? AuthenticateResult { get; set; } = result;
    }

    /// <summary>A circuit without a vendor context never asks for a company.</summary>
    private sealed class NoVendorUsers : IVendorUsers
    {
        public Task<Guid?> FindCompanyAsync(string userId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A circuit without a vendor context asks for no company.");

        public Task<Guid?> FindCurrentCompanyAsync(string userId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A circuit without a vendor context asks for no company.");
    }

    private sealed class NotMemberSource : IOrganizationMembershipSource
    {
        public List<(string Alias, string User)> Asked { get; } = [];

        public Task<OrganizationMembership> CheckAsync(string organizationAlias, string userId, CancellationToken cancellationToken)
        {
            lock (Asked)
            {
                Asked.Add((organizationAlias, userId));
            }

            return Task.FromResult(OrganizationMembership.NotMember);
        }
    }

    private sealed class ScriptedRevalidation(bool stillMember) : IMembershipRevalidation
    {
        private readonly TaskCompletionSource _twice = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public List<DateTimeOffset?> SignedInAt { get; } = [];

        public Task CalledTwice => _twice.Task;

        public Task<bool> IsStillMemberAsync(ClaimsPrincipal user, DateTimeOffset? signedInAt, CancellationToken cancellationToken = default)
        {
            lock (SignedInAt)
            {
                SignedInAt.Add(signedInAt);
            }

            if (Interlocked.Increment(ref _calls) == 2)
            {
                _twice.TrySetResult();
            }

            return Task.FromResult(stillMember);
        }

        public bool ClaimsHostOrganization(ClaimsPrincipal user) => user.HasClaim("organization", "acme");
    }

    private sealed class RecordingNavigationManager : NavigationManager
    {
        public RecordingNavigationManager(string baseUri, string uri) => Initialize(baseUri, uri);

        public List<(string Uri, bool ForceLoad)> Navigations { get; } = [];

        protected override void NavigateToCore(string uri, NavigationOptions options)
        {
            lock (Navigations)
            {
                Navigations.Add((ToAbsoluteUri(uri).AbsoluteUri, options.ForceLoad));
            }
        }
    }

    /// <summary>Queues posted work until <see cref="RunPending"/>, as a circuit's context runs it on its own turn.</summary>
    private sealed class QueueSynchronizationContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _pending = new();

        public bool PostedFromThreadPool { get; private set; }

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_pending)
            {
                PostedFromThreadPool |= Thread.CurrentThread.IsThreadPoolThread;
                _pending.Enqueue((d, state));
            }
        }

        public void RunPending()
        {
            while (true)
            {
                (SendOrPostCallback Callback, object? State) next;
                lock (_pending)
                {
                    if (!_pending.TryDequeue(out next))
                    {
                        return;
                    }
                }

                next.Callback(next.State);
            }
        }
    }
}
