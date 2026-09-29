using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;
using Platform.Web.Account;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// W-21 in an open Blazor circuit: the circuit's authentication state is revalidated every minute through the same
/// membership check as HTTP requests; when it fails the circuit's user becomes anonymous, the browser is sent to a full
/// reload of the page (whose request is challenged, so it shows the sign-in page), and no further event of that circuit
/// runs, even from a client that ignores the reload.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class CircuitRevalidationTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_circuit_whose_member_was_removed_turns_anonymous_and_reloads_into_the_sign_in_page()
    {
        var navigation = new RecordingNavigationManager("https://acme.localhost/", "https://acme.localhost/admin/staff");
        var guard = new CircuitSessionGuard(navigation, NullLogger<CircuitSessionGuard>.Instance);
        var context = new QueueSynchronizationContext();
        await OpenOnAsync(context, guard);
        var revalidation = new ScriptedRevalidation(stillMember: false);
        using var provider = new MembershipRevalidatingStateProvider(NullLoggerFactory.Instance, revalidation, guard, TimeSpan.FromMilliseconds(20));
        var signedOut = new TaskCompletionSource<AuthenticationState>();
        provider.AuthenticationStateChanged += async task =>
        {
            var changed = await task;
            if (changed.User.Identity?.IsAuthenticated != true)
            {
                signedOut.TrySetResult(changed);
            }
        };

        provider.SetAuthenticationState(Task.FromResult(new AuthenticationState(Member())));
        var state = await signedOut.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
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
        var guard = new CircuitSessionGuard(navigation, NullLogger<CircuitSessionGuard>.Instance);
        var revalidation = new ScriptedRevalidation(stillMember: true);
        using var provider = new MembershipRevalidatingStateProvider(NullLoggerFactory.Instance, revalidation, guard, TimeSpan.FromMilliseconds(20));

        provider.SetAuthenticationState(Task.FromResult(new AuthenticationState(Member())));
        await revalidation.CalledTwice.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        (await provider.GetAuthenticationStateAsync()).User.Identity?.IsAuthenticated.ShouldBe(true);
        guard.Ended.ShouldBeFalse();
        navigation.Navigations.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_ended_circuit_runs_no_further_event_and_sends_the_browser_to_sign_in_again()
    {
        var navigation = new RecordingNavigationManager("https://acme.localhost/", "https://acme.localhost/vendor/consent");
        var guard = new CircuitSessionGuard(navigation, NullLogger<CircuitSessionGuard>.Instance);
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
        await inbound(null!);
        context.RunPending();

        runs.ShouldBe(1);
        navigation.Navigations.ShouldBe([("https://acme.localhost/vendor/consent", true), ("https://acme.localhost/vendor/consent", true)]);
    }

    [Fact]
    public void The_host_revalidates_circuits_every_minute_so_a_removal_ends_a_circuit_within_five_minutes()
    {
        // Worst case: the membership confirmed just before the removal is reused for two minutes, and the circuit's next
        // revalidation comes at most one interval later.
        MembershipRevalidatingStateProvider.Interval.ShouldBe(TimeSpan.FromMinutes(1));
        (TimeSpan.FromMinutes(2) + MembershipRevalidatingStateProvider.Interval).ShouldBeLessThan(TimeSpan.FromMinutes(5));
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

    private static ClaimsPrincipal Member() =>
        new(new ClaimsIdentity([new Claim("sub", "acme.member"), new Claim("organization", "acme")], "test"));

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
