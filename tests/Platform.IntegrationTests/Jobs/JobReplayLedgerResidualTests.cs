using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Shared.Jobs;

namespace Platform.IntegrationTests.Jobs;

/// <summary>
/// W-42 pentest (PT-W42): characterises the boundary of <see cref="JobReplayLedger"/> on the live worker role. The ledger
/// binds a signature's nonce to one job id and refuses that nonce under a different id or again after the job succeeded; it
/// does not, however, serialise two runs of the <em>same</em> job id. Hangfire normally prevents that (a jobqueue row is
/// fetched once, under an invisibility timeout), but erp_app can write hangfire.jobqueue (no row-level security) and reset a
/// fetchedat, so for a future non-idempotent tenant-scoped job the worker could perform the same signed job id twice.
/// Today's six platform jobs are idempotent and bounded, so this is a defence-in-depth note, not a live vulnerability;
/// the fix for a non-idempotent job is DisableConcurrentExecution. This test pins the behaviour so a future change is seen.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class JobReplayLedgerResidualTests(DatabaseFixture db) : IAsyncLifetime
{
    private readonly NpgsqlDataSource _workerData = new NpgsqlDataSourceBuilder(db.WorkerConnectionString).Build();
    private readonly List<string> _jobIds = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_ledger_refuses_a_copied_id_and_a_completed_id_but_not_a_second_run_of_the_same_id()
    {
        var ledger = new JobReplayLedger(_workerData);
        var token = new JobToken(Guid.NewGuid(), DateTimeOffset.UtcNow);
        var original = Remember($"pt-w42-{Guid.NewGuid():N}");
        var copy = Remember($"pt-w42-{Guid.NewGuid():N}");

        // First run of the signature binds the nonce to the original job id: admitted.
        ledger.Claim(token, original).ShouldBeNull();

        // Control: the same nonce under a different job id (a copied row) is refused.
        ledger.Claim(token, copy).ShouldNotBeNull().ShouldContain("already ran as another job");

        // Residual: the same job id is admitted again before it succeeds (two concurrent fetches would both run).
        ledger.Claim(token, original).ShouldBeNull();
        ledger.Check(token, original).ShouldBeNull();

        // Control: once the job succeeds, the same job id is refused too.
        ledger.Complete(original);
        ledger.Claim(token, original).ShouldNotBeNull().ShouldContain("already succeeded");
        ledger.Check(token, original).ShouldNotBeNull().ShouldContain("already succeeded");
    }

    [Fact]
    public void An_unseen_signature_older_than_the_first_run_limit_is_refused()
    {
        var ledger = new JobReplayLedger(_workerData);
        var stale = new JobToken(Guid.NewGuid(), DateTimeOffset.UtcNow - JobAuthenticity.MaxAgeAtFirstRun - TimeSpan.FromDays(1));

        ledger.Claim(stale, Remember($"pt-w42-{Guid.NewGuid():N}"))
            .ShouldNotBeNull().ShouldContain("never ran");
    }

    private string Remember(string jobId)
    {
        _jobIds.Add(jobId);
        return jobId;
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        // Clean up the nonce rows this test bound (as the owner: erp_worker's policy needs an empty context, and the owner
        // bypasses row-level security); never touch another test's rows.
        if (_jobIds.Count > 0)
        {
            await using var owner = new NpgsqlConnection(db.OwnerConnectionString);
            await owner.OpenAsync(Ct);
            await using var command = new NpgsqlCommand("delete from platform.job_nonces where job_id = any(@ids)", owner);
            command.Parameters.AddWithValue("ids", _jobIds.ToArray());
            await command.ExecuteNonQueryAsync(Ct);
        }

        await _workerData.DisposeAsync();
    }
}
