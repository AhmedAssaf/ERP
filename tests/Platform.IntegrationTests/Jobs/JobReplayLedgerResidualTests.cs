using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Shared.Jobs;

namespace Platform.IntegrationTests.Jobs;

/// <summary>
/// W-42 pentest (PT-W42), closed in fix round 2: the boundary of <see cref="JobReplayLedger"/> on the live worker role. The
/// ledger binds a signature's nonce to one job id and refuses that nonce under a different id or again after the job
/// succeeded. A second run of the <em>same</em> job id while the first still runs (erp_app can write hangfire.jobqueue, and
/// Hangfire lets a Processing job be processed again) is refused by the run lock, a session advisory lock on the nonce held
/// for the whole run; once the run ends without success, the same id may run again (retries, the console's re-run).
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class JobReplayLedgerResidualTests(DatabaseFixture db) : IAsyncLifetime
{
    private readonly NpgsqlDataSource _workerData = new NpgsqlDataSourceBuilder(db.WorkerConnectionString).Build();
    private readonly List<string> _jobIds = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_ledger_refuses_a_copied_id_a_completed_id_and_a_second_run_of_the_same_id_while_the_first_runs()
    {
        var ledger = new JobReplayLedger(_workerData);
        var token = new JobToken(Guid.NewGuid(), DateTimeOffset.UtcNow);
        var original = Remember($"pt-w42-{Guid.NewGuid():N}");
        var copy = Remember($"pt-w42-{Guid.NewGuid():N}");

        // First run of the signature binds the nonce to the original job id: admitted.
        ledger.Claim(token, original).ShouldBeNull();

        // Control: the same nonce under a different job id (a copied row) is refused.
        ledger.Claim(token, copy).ShouldNotBeNull().ShouldContain("already ran as another job");

        // Fix round 2: while a run holds the nonce's run lock, a second run of the same id cannot take it.
        using (var running = ledger.TryLockRun(token))
        {
            running.ShouldNotBeNull();
            ledger.TryLockRun(token).ShouldBeNull("a second run of the same job id is refused as already running");
            ledger.IsRunning(token).ShouldBeTrue();
        }

        // The run ended without success: the same job id may run again (a retry, the console's re-run).
        ledger.IsRunning(token).ShouldBeFalse();
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
