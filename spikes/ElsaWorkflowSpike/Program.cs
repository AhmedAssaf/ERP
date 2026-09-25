using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Activities.Flowchart.Models;
using Elsa.Workflows.Models;
using Elsa.Workflows.Options;
using Elsa.Workflows.State;
using Microsoft.Extensions.DependencyInjection;
using ElsaWorkflowSpike;

// W-20 spike: can Elsa 3 execute a tenant's approval chain while the fixed points hold?
// Elsa is used as a library only: no Elsa database. The workflow JSON and the execution
// state are strings on our Tender row, so PostgreSQL stays the single source of truth.

var results = new List<(string Check, bool Pass, string Note)>();
void Check(string name, bool pass, string note = "") { results.Add((name, pass, note)); Console.WriteLine($"{(pass ? "PASS" : "FAIL")}  {name}  {note}"); }

// ---------- Q1a: a valid chain runs end to end, with a process restart between every step ----------
{
    var tender = NewTender("T-valid");
    await Publish(tender, ValidChain());
    await StartTender(tender);
    await Decide(tender, "u.legal", "Approved");      // contracts screening, any-of
    await Decide(tender, "u.tech1", "Approved");      // technical committee, any-of
    // LockScores and OpenFinancial run automatically; finance approval is all-of.
    await Decide(tender, "u.cfo", "Approved");
    var mid = await Status(tender);
    await Decide(tender, "u.fm", "Approved");
    var end = await Status(tender);
    Check("Q1a valid chain completes across restarts", end == WorkflowStatus.Finished && tender.ScoresLocked && tender.FinancialOpened,
        $"after cfo only: {mid}; after fm: {end}; audit: {string.Join(" | ", tender.Audit)}");
}

// ---------- Q1b: a definition that opens financial before locking is rejected at publish ----------
{
    var bad = new Workflow { Root = new Sequence { Activities = { Approval("contracts", "u.legal"), new OpenFinancial(), new LockScores() } } };
    var errors = DefinitionValidator.Validate(bad);
    Check("Q1b publish rejects financial-before-lock (Sequence)", errors.Count > 0, string.Join("; ", errors));

    var skip = new Workflow { Root = new Sequence { Activities = { Approval("contracts", "u.legal"), new OpenFinancial() } } };
    errors = DefinitionValidator.Validate(skip);
    Check("Q1b publish rejects a chain that skips locking", errors.Count > 0, string.Join("; ", errors));
}

// ---------- Q1c: the same bad definition forced past validation still cannot open early ----------
{
    var tender = NewTender("T-forced");
    tender.WorkflowSnapshotJson = Serialize(new Workflow { Root = new Sequence { Activities = { Approval("contracts", "u.legal"), new OpenFinancial(), new LockScores() } } });
    await StartTender(tender);
    await Decide(tender, "u.legal", "Approved");
    var (status, sub, incidents) = await Detail(tender);
    Check("Q1c runtime guard stops forced bad definition", !tender.FinancialOpened && sub == WorkflowSubStatus.Faulted,
        $"status {status}/{sub}; incident: {incidents}");
}

// ---------- Q1d: a Studio-style flowchart with a branch that bypasses locking is rejected ----------
{
    var start = new Start { Id = "start" };
    var contracts = Approval("contracts", "u.legal"); contracts.Id = "contracts";
    var tech = Approval("technical", "u.tech1"); tech.Id = "technical";
    var lockScores = new LockScores { Id = "lock" };
    var open = new OpenFinancial { Id = "open" };
    var flow = new Flowchart
    {
        Start = start,
        Activities = { start, contracts, tech, lockScores, open },
        Connections =
        {
            new Connection(start, contracts),
            new Connection(new Endpoint(contracts, "Approved"), new Endpoint(tech)),
            new Connection(new Endpoint(tech, "Approved"), new Endpoint(lockScores)),
            new Connection(lockScores, open),
            // The trap: "rejected by technical" jumps straight to financial opening.
            new Connection(new Endpoint(tech, "Rejected"), new Endpoint(open)),
        }
    };
    var errors = DefinitionValidator.Validate(new Workflow { Root = flow });
    Check("Q1d publish rejects flowchart branch that bypasses locking", errors.Count > 0, string.Join("; ", errors));
}

// ---------- Q2: a running tender keeps its snapshot after the definition changes ----------
{
    var tender = NewTender("T-snapshot");
    var liveDefinition = ValidChain();
    await Publish(tender, liveDefinition);
    await StartTender(tender);
    await Decide(tender, "u.legal", "Approved");

    // The tenant edits the live definition: the technical step now needs someone else, and a new step is added.
    liveDefinition = new Workflow { Root = new Sequence { Activities = {
        Approval("contracts", "u.legal"), Approval("technical", "u.newtech"), Approval("procurement-head", "u.head"),
        new LockScores(), new OpenFinancial(), Approval("finance", "u.cfo") } } };

    // After a restart the tender resumes from its own snapshot, so the old approver still decides.
    await Decide(tender, "u.tech1", "Approved");
    await Decide(tender, "u.cfo", "Approved");
    await Decide(tender, "u.fm", "Approved");
    var end = await Status(tender);
    var newTender = NewTender("T-after-edit");
    await Publish(newTender, liveDefinition);
    await StartTender(newTender);
    var pending = await PendingStep(newTender);
    Check("Q2 running tender keeps its snapshot", end == WorkflowStatus.Finished && !tender.Audit.Any(a => a.Contains("u.newtech")),
        $"old tender finished on v1; a tender published after the edit waits on '{pending}' as expected");
}

// ---------- Guard: a user who is not on the step cannot decide ----------
{
    var tender = NewTender("T-wrong-user");
    await Publish(tender, ValidChain());
    await StartTender(tender);
    await Decide(tender, "u.intruder", "Approved");
    var afterIntruder = await PendingStep(tender);
    await Decide(tender, "u.legal", "Approved");
    var afterLegal = await PendingStep(tender);
    var (status, sub, _) = await Detail(tender);
    Check("Guard non-approver refused, tender keeps running", afterIntruder == "contracts" && afterLegal == "technical" && sub == WorkflowSubStatus.Suspended,
        $"after intruder waits on '{afterIntruder}', after u.legal waits on '{afterLegal}', status {status}/{sub}");
}

// ---------- Measurements for the ADR ----------
{
    var tender = TenderStore.Get("T-valid");
    Console.WriteLine();
    Console.WriteLine($"Snapshot JSON (5-step chain): {tender.WorkflowSnapshotJson!.Length:N0} chars");
    Console.WriteLine($"State JSON after finish:     {tender.WorkflowStateJson!.Length:N0} chars");
    var t = NewTender("T-timing");
    await Publish(t, ValidChain());
    await StartTender(t);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    await Decide(t, "u.legal", "Approved");
    Console.WriteLine($"One decision incl. fresh container, load, resume, save: {sw.ElapsedMilliseconds} ms");
    sw.Restart();
    var sp = await Host();
    Console.WriteLine($"Of which building the Elsa container and registry: {sw.ElapsedMilliseconds} ms");
}

Console.WriteLine();
Console.WriteLine($"{results.Count(r => r.Pass)}/{results.Count} checks passed.");
return results.All(r => r.Pass) ? 0 : 1;

// ================= helpers =================

static Workflow ValidChain() => new()
{
    Root = new Sequence
    {
        Activities =
        {
            Approval("contracts", "u.legal"),
            Approval("technical", "u.tech1,u.tech2"),
            new LockScores(),
            new OpenFinancial(),
            Approval("finance", "u.cfo,u.fm", "AllOf"),
        }
    }
};

static ApprovalStep Approval(string name, string approvers, string rule = "AnyOf") =>
    new() { StepName = new(name), Department = new(name), Approvers = new(approvers), Rule = new(rule) };

static Tender NewTender(string id) => TenderStore.Tenders[id] = new Tender(id);

// A fresh container for every call: nothing survives in memory between steps except the Tender row.
static async Task<IServiceProvider> Host()
{
    var services = new ServiceCollection();
    services.AddElsa(elsa => elsa.AddActivitiesFrom<ApprovalStep>());
    var sp = services.BuildServiceProvider();
    await sp.GetRequiredService<Elsa.Workflows.Management.IActivityRegistryPopulator>().PopulateRegistryAsync(default);
    return sp;
}

static string Serialize(Workflow workflow)
{
    var sp = Host().GetAwaiter().GetResult();
    return sp.GetRequiredService<IActivitySerializer>().Serialize(workflow);
}

static async Task Publish(Tender tender, Workflow workflow)
{
    var errors = DefinitionValidator.Validate(workflow);
    if (errors.Count > 0) throw new InvalidOperationException(string.Join("; ", errors));
    tender.WorkflowSnapshotJson = Serialize(workflow);
    await Task.CompletedTask;
}

static async Task<(IServiceProvider, Workflow)> Load(Tender tender)
{
    var sp = await Host();
    var workflow = sp.GetRequiredService<IActivitySerializer>().Deserialize<Workflow>(tender.WorkflowSnapshotJson!);
    return (sp, workflow);
}

static async Task StartTender(Tender tender)
{
    var (sp, workflow) = await Load(tender);
    var result = await sp.GetRequiredService<IWorkflowRunner>().RunAsync(workflow, new RunWorkflowOptions { CorrelationId = tender.Id });
    tender.WorkflowStateJson = sp.GetRequiredService<IWorkflowStateSerializer>().Serialize(result.WorkflowState);
}

static async Task Decide(Tender tender, string actor, string decision)
{
    var (sp, workflow) = await Load(tender);
    var state = sp.GetRequiredService<IWorkflowStateSerializer>().Deserialize(tender.WorkflowStateJson!);
    var bookmark = state.Bookmarks.Single();
    var result = await sp.GetRequiredService<IWorkflowRunner>().RunAsync(workflow, state, new RunWorkflowOptions
    {
        BookmarkId = bookmark.Id,
        Input = new Dictionary<string, object> { ["Actor"] = actor, ["Decision"] = decision },
    });
    tender.WorkflowStateJson = sp.GetRequiredService<IWorkflowStateSerializer>().Serialize(result.WorkflowState);
}

static async Task<WorkflowState> State(Tender tender)
{
    var sp = await Host();
    return sp.GetRequiredService<IWorkflowStateSerializer>().Deserialize(tender.WorkflowStateJson!);
}

static async Task<WorkflowStatus> Status(Tender tender) => (await State(tender)).Status;

static async Task<(WorkflowStatus, WorkflowSubStatus, string)> Detail(Tender tender)
{
    var s = await State(tender);
    return (s.Status, s.SubStatus, string.Join("; ", s.Incidents.Select(i => i.Exception?.Message)));
}

static async Task<string> PendingStep(Tender tender)
{
    var s = await State(tender);
    var payload = s.Bookmarks.Single().Payload;
    // Bookmark payloads come back from JSON as ExpandoObject, not as the record type.
    return payload switch
    {
        ApprovalBookmark b => b.Step,
        IDictionary<string, object?> d => d.FirstOrDefault(kv => kv.Key.Equals("Step", StringComparison.OrdinalIgnoreCase)).Value?.ToString() ?? $"(keys: {string.Join(",", d.Keys)})",
        _ => payload?.ToString() ?? "(none)",
    };
}
