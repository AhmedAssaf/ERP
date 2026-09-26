using System.Text.Json;
using Platform.Modules.Workflow;
using Platform.Modules.Workflow.Contracts;

namespace Platform.UnitTests.Workflow;

public class SnapshotDocumentTests
{
    [Fact]
    public void The_v1_json_keeps_its_property_names()
    {
        var json = SnapshotDocument.Serialize(Guid.CreateVersion7(), 3, "Chain", DefaultTemplate.Steps);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        root.EnumerateObject().Select(p => p.Name).ShouldBe(["version", "definitionId", "definitionVersion", "name", "steps"]);
        root.GetProperty("version").GetInt32().ShouldBe(SnapshotDocument.CurrentVersion);
        var step = root.GetProperty("steps")[1];
        step.EnumerateObject().Select(p => p.Name).ShouldBe(["stage", "department", "rule", "actorRoles", "threshold"]);
        step.GetProperty("stage").GetString().ShouldBe("TechnicalScoring");
        step.GetProperty("rule").GetString().ShouldBe("AllOf");
        step.GetProperty("actorRoles")[0].GetString().ShouldBe("evaluator");
    }
}
