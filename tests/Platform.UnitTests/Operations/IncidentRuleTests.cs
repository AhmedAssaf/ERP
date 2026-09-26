using Platform.Modules.Operations;
using Platform.Modules.Operations.Contracts;

namespace Platform.UnitTests.Operations;

/// <summary>F-60 opening and closing rule, tested against the pure decision (no database).</summary>
public sealed class IncidentRuleTests
{
    [Fact]
    public void A_failure_opens_one_incident_and_repeated_failures_do_not_open_more()
    {
        IncidentRule.Evaluate(HealthStatus.Unhealthy, hasOpenIncident: false).ShouldBe(IncidentRule.Outcome.Open);

        // Once the first failure opened an incident, a further failure while it is still open opens no second one.
        IncidentRule.Evaluate(HealthStatus.Unhealthy, hasOpenIncident: true).ShouldBe(IncidentRule.Outcome.None);
    }

    [Fact]
    public void A_recovery_closes_the_open_incident() =>
        IncidentRule.Evaluate(HealthStatus.Healthy, hasOpenIncident: true).ShouldBe(IncidentRule.Outcome.Close);

    [Fact]
    public void Degraded_does_not_open_an_incident()
    {
        IncidentRule.Evaluate(HealthStatus.Degraded, hasOpenIncident: false).ShouldBe(IncidentRule.Outcome.None);
        IncidentRule.Evaluate(HealthStatus.Degraded, hasOpenIncident: true).ShouldBe(IncidentRule.Outcome.None);
    }

    [Fact]
    public void A_healthy_result_with_no_open_incident_does_nothing() =>
        IncidentRule.Evaluate(HealthStatus.Healthy, hasOpenIncident: false).ShouldBe(IncidentRule.Outcome.None);
}
