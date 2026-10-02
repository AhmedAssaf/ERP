using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Modules.Vendors;
using Platform.Modules.Vendors.RateLimiting;

namespace Platform.UnitTests.Vendors;

/// <summary>
/// W-37: joins are limited per user and per tenant over a minute; W-35: consent changes (grants and revocations together)
/// per vendor company over an hour. The limits come from <c>Vendors:*</c> settings; a refusal is logged as a warning with
/// ids only.
/// </summary>
public sealed class VendorRateLimitsTests
{
    private static readonly Guid Acme = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Beta = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void The_defaults_are_five_joins_per_user_and_twenty_per_tenant_a_minute_and_thirty_consent_changes_per_company_an_hour()
    {
        var options = new VendorsOptions();

        options.JoinsPerUserPerMinute.ShouldBe(5);
        options.JoinsPerTenantPerMinute.ShouldBe(20);
        options.ConsentChangesPerCompanyPerHour.ShouldBe(30);
    }

    [Fact]
    public void A_user_over_its_limit_is_refused_on_every_tenant_and_another_user_is_not()
    {
        var clock = new SlidingWindowLimiterTests.ManualClock();
        var logs = new Logs();
        var limits = Limits(clock, logs, perUser: 2, perTenant: 100);

        limits.TryJoin(Acme, "u1").ShouldBeTrue();
        limits.TryJoin(Beta, "u1").ShouldBeTrue();
        limits.TryJoin(Beta, "u1").ShouldBeFalse();
        limits.TryJoin(Acme, "u1").ShouldBeFalse();
        limits.TryJoin(Beta, "u2").ShouldBeTrue();

        var warning = logs.Entries.First();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldContain("user u1");
        warning.Message.ShouldContain(Beta.ToString());
        warning.Message.ShouldContain("per-user");

        clock.Advance(TimeSpan.FromMinutes(1));
        limits.TryJoin(Acme, "u1").ShouldBeTrue();
    }

    [Fact]
    public void A_tenant_over_its_limit_refuses_every_user_while_another_tenant_is_not_affected()
    {
        var clock = new SlidingWindowLimiterTests.ManualClock();
        var logs = new Logs();
        var limits = Limits(clock, logs, perUser: 100, perTenant: 2);

        limits.TryJoin(Beta, "u1").ShouldBeTrue();
        limits.TryJoin(Beta, "u2").ShouldBeTrue();
        limits.TryJoin(Beta, "u3").ShouldBeFalse();
        limits.TryJoin(Acme, "u3").ShouldBeTrue();

        logs.Entries.ShouldHaveSingleItem().Message.ShouldContain("per-tenant");

        clock.Advance(TimeSpan.FromMinutes(1));
        limits.TryJoin(Beta, "u3").ShouldBeTrue();
    }

    [Fact]
    public void A_user_refused_by_its_own_limit_does_not_use_up_the_tenants_limit()
    {
        var limits = Limits(new SlidingWindowLimiterTests.ManualClock(), new Logs(), perUser: 1, perTenant: 2);

        limits.TryJoin(Beta, "noisy").ShouldBeTrue();
        for (var i = 0; i < 10; i++)
        {
            limits.TryJoin(Beta, "noisy").ShouldBeFalse();
        }

        limits.TryJoin(Beta, "quiet").ShouldBeTrue("the noisy user's refusals never reached the tenant's count");
    }

    [Fact]
    public void Grants_and_revocations_share_one_limit_per_company_for_an_hour()
    {
        var clock = new SlidingWindowLimiterTests.ManualClock();
        var logs = new Logs();
        var limits = Limits(clock, logs, consentPerHour: 2);
        var company = Guid.NewGuid();
        var other = Guid.NewGuid();

        limits.TryChangeConsent(company, "admin", "grant").ShouldBeTrue();
        limits.TryChangeConsent(company, "admin", "revoke").ShouldBeTrue();
        limits.TryChangeConsent(company, "admin", "grant").ShouldBeFalse();
        limits.TryChangeConsent(company, "second-admin", "revoke").ShouldBeFalse("the limit is the company's, not the user's");
        limits.TryChangeConsent(other, "admin", "grant").ShouldBeTrue();

        var warning = logs.Entries.First();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldContain(company.ToString());
        warning.Message.ShouldContain("user admin");
        warning.Message.ShouldContain("grant");

        clock.Advance(TimeSpan.FromMinutes(59));
        limits.TryChangeConsent(company, "admin", "grant").ShouldBeFalse();
        clock.Advance(TimeSpan.FromMinutes(1));
        limits.TryChangeConsent(company, "admin", "grant").ShouldBeTrue();
    }

    private static VendorRateLimits Limits(
        TimeProvider clock, Logs logs, int perUser = 5, int perTenant = 20, int consentPerHour = 30) =>
        new(
            Options.Create(new VendorsOptions
            {
                JoinsPerUserPerMinute = perUser,
                JoinsPerTenantPerMinute = perTenant,
                ConsentChangesPerCompanyPerHour = consentPerHour,
            }),
            clock,
            logs);

    private sealed class Logs : ILogger<VendorRateLimits>
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception)));
    }
}
