using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Modules.Vendors;
using Platform.Modules.Vendors.RateLimiting;

namespace Platform.UnitTests.Vendors;

/// <summary>
/// W-37: every join is limited per user, and a first-time join also per tenant, over a minute; W-35: consent grants (not
/// revocations) per vendor company over an hour. The limits come from <c>Vendors:*</c> settings; a refusal is logged as a
/// warning with ids only, the first one per limit and key in each window.
/// </summary>
public sealed class VendorRateLimitsTests
{
    private static readonly Guid Acme = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Beta = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void The_defaults_are_five_joins_per_user_and_twenty_per_tenant_a_minute_and_thirty_consent_grants_per_company_an_hour()
    {
        var options = new VendorsOptions();

        options.JoinsPerUserPerMinute.ShouldBe(5);
        options.JoinsPerTenantPerMinute.ShouldBe(20);
        options.ConsentGrantsPerCompanyPerHour.ShouldBe(30);
        options.MaxConcurrentJoins.ShouldBe(10);
    }

    [Fact]
    public void A_user_over_its_limit_is_refused_on_every_tenant_and_another_user_is_not()
    {
        var clock = new SlidingWindowLimiterTests.ManualClock();
        var logs = new Logs();
        var limits = Limits(clock, logs, perUser: 2, perTenant: 100);

        limits.TryJoinAsUser(Acme, "u1").ShouldBeTrue();
        limits.TryJoinAsUser(Beta, "u1").ShouldBeTrue();
        limits.TryJoinAsUser(Beta, "u1").ShouldBeFalse();
        limits.TryJoinAsUser(Acme, "u1").ShouldBeFalse();
        limits.TryJoinAsUser(Beta, "u2").ShouldBeTrue();

        var warning = logs.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldContain("user u1");
        warning.Message.ShouldContain(Beta.ToString());
        warning.Message.ShouldContain("per-user");

        clock.Advance(TimeSpan.FromMinutes(1));
        limits.TryJoinAsUser(Acme, "u1").ShouldBeTrue();
    }

    [Fact]
    public void A_tenant_over_its_limit_refuses_every_user_while_another_tenant_is_not_affected()
    {
        var clock = new SlidingWindowLimiterTests.ManualClock();
        var logs = new Logs();
        var limits = Limits(clock, logs, perUser: 100, perTenant: 2);

        limits.TryJoinTenant(Beta, "u1").ShouldBeTrue();
        limits.TryJoinTenant(Beta, "u2").ShouldBeTrue();
        limits.TryJoinTenant(Beta, "u3").ShouldBeFalse();
        limits.TryJoinTenant(Acme, "u3").ShouldBeTrue();

        logs.Entries.ShouldHaveSingleItem().Message.ShouldContain("per-tenant");

        clock.Advance(TimeSpan.FromMinutes(1));
        limits.TryJoinTenant(Beta, "u3").ShouldBeTrue();
    }

    [Fact]
    public void The_user_and_tenant_limits_are_counted_apart()
    {
        var limits = Limits(new SlidingWindowLimiterTests.ManualClock(), new Logs(), perUser: 1, perTenant: 2);

        limits.TryJoinAsUser(Beta, "noisy").ShouldBeTrue();
        for (var i = 0; i < 10; i++)
        {
            limits.TryJoinAsUser(Beta, "noisy").ShouldBeFalse();
        }

        limits.TryJoinTenant(Beta, "quiet").ShouldBeTrue("the noisy user's joins never reached the tenant's count");
        limits.TryJoinTenant(Beta, "quiet-too").ShouldBeTrue();
    }

    [Fact]
    public void Grants_are_limited_per_company_for_an_hour()
    {
        var clock = new SlidingWindowLimiterTests.ManualClock();
        var logs = new Logs();
        var limits = Limits(clock, logs, grantsPerHour: 2);
        var company = Guid.NewGuid();
        var other = Guid.NewGuid();

        limits.TryGrantConsent(company, "admin").ShouldBeTrue();
        limits.TryGrantConsent(company, "admin").ShouldBeTrue();
        limits.TryGrantConsent(company, "admin").ShouldBeFalse();
        limits.TryGrantConsent(company, "second-admin").ShouldBeFalse("the limit is the company's, not the user's");
        limits.TryGrantConsent(other, "admin").ShouldBeTrue();

        var warning = logs.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldContain(company.ToString());
        warning.Message.ShouldContain("user admin");
        warning.Message.ShouldContain("grant");

        clock.Advance(TimeSpan.FromMinutes(59));
        limits.TryGrantConsent(company, "admin").ShouldBeFalse();
        clock.Advance(TimeSpan.FromMinutes(1));
        limits.TryGrantConsent(company, "admin").ShouldBeTrue();
    }

    [Fact]
    public void Only_the_first_refusal_per_limit_and_key_in_a_window_is_logged()
    {
        var clock = new SlidingWindowLimiterTests.ManualClock();
        var logs = new Logs();
        var limits = Limits(clock, logs, perUser: 1, perTenant: 1, grantsPerHour: 1);
        var company = Guid.NewGuid();
        limits.TryJoinAsUser(Beta, "u1").ShouldBeTrue();
        limits.TryJoinTenant(Beta, "u1").ShouldBeTrue();
        limits.TryGrantConsent(company, "admin").ShouldBeTrue();

        for (var i = 0; i < 50; i++)
        {
            limits.TryJoinAsUser(Beta, "u1").ShouldBeFalse();
            limits.TryJoinTenant(Beta, "u2").ShouldBeFalse();
            limits.TryGrantConsent(company, "admin").ShouldBeFalse();
        }

        logs.Entries.Count.ShouldBe(3, "one per user, tenant and company");

        // A minute on: the join limits are open again, and their next refusal is logged once more; the hour of the grant
        // refusal is not over, so it stays quiet.
        clock.Advance(TimeSpan.FromMinutes(1));
        limits.TryJoinAsUser(Beta, "u1").ShouldBeTrue();
        limits.TryJoinAsUser(Beta, "u1").ShouldBeFalse();
        limits.TryGrantConsent(company, "admin").ShouldBeFalse();

        logs.Entries.Count.ShouldBe(4);
    }

    private static VendorRateLimits Limits(
        TimeProvider clock, Logs logs, int perUser = 5, int perTenant = 20, int grantsPerHour = 30) =>
        new(
            Options.Create(new VendorsOptions
            {
                JoinsPerUserPerMinute = perUser,
                JoinsPerTenantPerMinute = perTenant,
                ConsentGrantsPerCompanyPerHour = grantsPerHour,
            }),
            clock,
            logs);

    internal sealed class Logs : ILogger<VendorRateLimits>, ILogger<ConcurrentJoinGate>
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception)));
    }
}
