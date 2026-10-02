using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Modules.Vendors;
using Platform.Modules.Vendors.RateLimiting;

namespace Platform.UnitTests.Vendors;

/// <summary>
/// W-37: at most <c>Vendors:MaxConcurrentJoins</c> first-time joins hold their database connection across the Keycloak add
/// at once in one web instance; one more waits briefly for a slot and is then refused.
/// </summary>
public sealed class ConcurrentJoinGateTests
{
    private static readonly Guid Tenant = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void The_default_is_ten_joins_in_flight()
    {
        new VendorsOptions().MaxConcurrentJoins.ShouldBe(10);
        using var gate = new ConcurrentJoinGate(Options.Create(new VendorsOptions()), NullLogger<ConcurrentJoinGate>.Instance);
        gate.Available.ShouldBe(10);
        ConcurrentJoinGate.DefaultWait.ShouldBe(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task A_join_over_the_cap_is_refused_after_the_wait_and_a_released_slot_lets_the_next_one_in()
    {
        using var gate = new ConcurrentJoinGate(2, TimeSpan.FromMilliseconds(50), NullLogger<ConcurrentJoinGate>.Instance);
        var ct = TestContext.Current.CancellationToken;

        var first = (await gate.TryEnterAsync(Tenant, "u1", ct)).ShouldNotBeNull();
        using var second = (await gate.TryEnterAsync(Tenant, "u2", ct)).ShouldNotBeNull();

        (await gate.TryEnterAsync(Tenant, "u3", ct)).ShouldBeNull();

        first.Dispose();
        first.Dispose(); // a slot is released once, however often it is disposed
        gate.Available.ShouldBe(1);
        using var third = (await gate.TryEnterAsync(Tenant, "u3", ct)).ShouldNotBeNull();
        gate.Available.ShouldBe(0);
    }

    [Fact]
    public async Task A_waiting_join_gets_a_slot_released_within_the_wait()
    {
        using var gate = new ConcurrentJoinGate(1, TimeSpan.FromSeconds(30), NullLogger<ConcurrentJoinGate>.Instance);
        var ct = TestContext.Current.CancellationToken;
        var held = (await gate.TryEnterAsync(Tenant, "u1", ct)).ShouldNotBeNull();

        var waiting = gate.TryEnterAsync(Tenant, "u2", ct);
        waiting.IsCompleted.ShouldBeFalse();
        held.Dispose();

        using var slot = (await waiting).ShouldNotBeNull();
    }
}
