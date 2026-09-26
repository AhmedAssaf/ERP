using Platform.Modules.Identity;

namespace Platform.UnitTests.Identity;

/// <summary>
/// Member counts come from the Keycloak Admin API (plan task 9) and the console's tenant list asks for every tenant on
/// each load, so a known count is reused for <see cref="CachingOrganizationMembers.CacheFor"/> per organization.
/// </summary>
public sealed class CachingOrganizationMembersTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_count_is_reused_for_sixty_seconds_per_organization()
    {
        var source = new CountingSource(new() { ["acme"] = 4, ["beta"] = 2 });
        var clock = new ManualClock();
        var members = new CachingOrganizationMembers(source, clock);

        (await members.CountAsync("acme", Ct)).ShouldBe(4);
        source.Counts["acme"] = 5;
        clock.Advance(TimeSpan.FromSeconds(59));
        (await members.CountAsync("acme", Ct)).ShouldBe(4);
        (await members.CountAsync("beta", Ct)).ShouldBe(2);
        source.Calls.ShouldBe(2);

        clock.Advance(TimeSpan.FromSeconds(1));
        (await members.CountAsync("acme", Ct)).ShouldBe(5);
        source.Calls.ShouldBe(3);
        CachingOrganizationMembers.CacheFor.ShouldBe(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task An_unknown_count_is_not_cached()
    {
        var source = new CountingSource([]);
        var members = new CachingOrganizationMembers(source, new ManualClock());

        (await members.CountAsync("acme", Ct)).ShouldBeNull();
        source.Counts["acme"] = 3;
        (await members.CountAsync("acme", Ct)).ShouldBe(3);
        source.Calls.ShouldBe(2);
    }

    private sealed class CountingSource(Dictionary<string, int> counts) : IOrganizationMemberSource
    {
        public Dictionary<string, int> Counts { get; } = counts;

        public int Calls { get; private set; }

        public Task<int?> CountAsync(string organizationAlias, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Counts.TryGetValue(organizationAlias, out var count) ? count : (int?)null);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 27, 8, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
