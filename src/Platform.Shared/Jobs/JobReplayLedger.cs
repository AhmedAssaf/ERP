using Npgsql;

namespace Platform.Shared.Jobs;

/// <summary>
/// W-42: each signed job's nonce runs under one job id only, and not again once that job succeeded. The application role
/// can copy a signed row's invocation and parameters into a new job row, or move a succeeded job back to the queue; the
/// signature still matches, but the first run binds the nonce to its job id in <c>platform.job_nonces</c>, which only the
/// worker's role may read or write (jobs migrations 0001 and 0002), and a successful run marks it completed, so both are
/// refused. A retry and a re-run from the console follow a failure, keep their job id and run again. A nonce never seen
/// before is accepted only while its signature is younger than <see cref="JobAuthenticity.MaxAgeAtFirstRun"/>; rows are
/// kept that long and as long as their job exists, so an old copy cannot slip in after its row is pruned. Hangfire's
/// storage API is synchronous, and so is this: it runs on the worker's job thread.
/// </summary>
internal sealed class JobReplayLedger(NpgsqlDataSource dataSource)
{
    private const string BoundRow = "select job_id, completed_at is not null from platform.job_nonces where nonce = @nonce";

    /// <summary>Binds the nonce to <paramref name="jobId"/> on its first run; the refusal when it may not run.</summary>
    public string? Claim(JobToken token, string jobId)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        using var connection = dataSource.OpenConnection();
        var bound = Read(connection, $"""
            with claimed as (
                insert into platform.job_nonces (nonce, job_id, signed_at)
                select @nonce, @job_id, @signed_at
                where @signed_at >= now() - @max_age
                on conflict (nonce) do nothing
                returning job_id, false
            )
            select * from claimed
            union all
            {BoundRow}
            limit 1
            """, token, jobId);

        // A claim that lost a race to a concurrent run sees neither its own insert nor the winner's row in its snapshot: a
        // second statement, with a new snapshot, finds the winner.
        if (bound is null && IsYoung(token))
        {
            bound = Read(connection, BoundRow, token, jobId);
            if (bound is null)
            {
                return "the job's signature was claimed by another run at the same time";
            }
        }

        return Outcome(bound, jobId);
    }

    /// <summary>The same decision without binding anything (the retry election).</summary>
    public string? Check(JobToken token, string jobId)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        using var connection = dataSource.OpenConnection();
        var bound = Read(connection, BoundRow, token, jobId);
        return bound is null && IsYoung(token) ? null : Outcome(bound, jobId);
    }

    /// <summary>Marks the nonces bound to <paramref name="jobId"/> completed: the job succeeded and never runs again.</summary>
    public void Complete(string jobId)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand(
            "update platform.job_nonces set completed_at = now() where job_id = @job_id and completed_at is null", connection);
        command.Parameters.AddWithValue("job_id", jobId);
        command.ExecuteNonQuery();
    }

    /// <summary>Removes nonces older than the first-run limit (and a day) whose job no longer exists; returns how many.</summary>
    public int Prune()
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
            delete from platform.job_nonces n
            where n.signed_at < now() - @max_age - interval '1 day'
              and not exists (select 1 from hangfire.job j where j.id::text = n.job_id)
            """, connection);
        command.Parameters.AddWithValue("max_age", JobAuthenticity.MaxAgeAtFirstRun);
        return command.ExecuteNonQuery();
    }

    private static bool IsYoung(JobToken token) => token.SignedAt >= DateTimeOffset.UtcNow - JobAuthenticity.MaxAgeAtFirstRun;

    private static (string JobId, bool Completed)? Read(NpgsqlConnection connection, string sql, JobToken token, string jobId)
    {
#pragma warning disable CA2100 // The ledger's own statements; values are parameters.
        using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        command.Parameters.AddWithValue("nonce", token.Nonce);
        command.Parameters.AddWithValue("job_id", jobId);
        command.Parameters.AddWithValue("signed_at", token.SignedAt);
        command.Parameters.AddWithValue("max_age", JobAuthenticity.MaxAgeAtFirstRun);
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetBoolean(1)) : null;
    }

    private static string? Outcome((string JobId, bool Completed)? bound, string jobId) => bound switch
    {
        null => $"the job's signature is older than {JobAuthenticity.MaxAgeAtFirstRun.TotalDays:0} days and it never ran",
        { JobId: var other } when !string.Equals(other, jobId, StringComparison.Ordinal) => "the job's signature already ran as another job (a copied row)",
        { Completed: true } => "the job already succeeded; a succeeded job does not run again",
        _ => null,
    };
}
