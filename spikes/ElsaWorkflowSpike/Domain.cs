namespace ElsaWorkflowSpike;

// Stands in for the tender aggregate in PostgreSQL. The fixed points (F-30 locking,
// financial opening) are enforced here, not in the workflow: the domain refuses an
// out-of-order call no matter which executor or definition asks for it.
public sealed class Tender(string id)
{
    public string Id { get; } = id;
    public bool ScoresLocked { get; private set; }
    public bool FinancialOpened { get; private set; }
    public List<string> Audit { get; } = [];

    // What our own table would hold: the Elsa workflow JSON taken at publishing,
    // and the serialized execution state after each step.
    public string? WorkflowSnapshotJson { get; set; }
    public string? WorkflowStateJson { get; set; }

    public void LockScores(string actor)
    {
        if (ScoresLocked) throw new InvariantViolation("Scores are already locked.");
        ScoresLocked = true;
        Audit.Add($"scores locked by {actor}");
    }

    public void OpenFinancial(string actor)
    {
        if (!ScoresLocked) throw new InvariantViolation("Financial envelopes cannot open before technical scores are locked (F-30).");
        FinancialOpened = true;
        Audit.Add($"financial opened by {actor}");
    }

    public void RecordDecision(string step, string actor, string decision) =>
        Audit.Add($"{step}: {decision} by {actor}");
}

public sealed class InvariantViolation(string message) : Exception(message);

// Survives "process restarts" in the spike, the way the database would.
public static class TenderStore
{
    public static readonly Dictionary<string, Tender> Tenders = new();
    public static Tender Get(string id) => Tenders[id];
}
