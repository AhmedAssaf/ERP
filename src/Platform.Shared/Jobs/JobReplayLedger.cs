using Npgsql;

namespace Platform.Shared.Jobs;

/// <summary>
/// W-42: each signed job's nonce runs under one job id only. The application role can copy a signed row's invocation and
/// parameters into a new job row; the signature still matches, but the first run binds the nonce to its job id in
/// <c>platform.job_nonces</c>, which only the worker's role may read or write (jobs migration 0001), so the copy is
/// refused. A retry and a re-run from the console keep their job id and run again. A nonce never seen before is accepted
/// only while its signature is younger than <see cref="JobAuthenticity.MaxAgeAtFirstRun"/>; rows are kept that long and
/// as long as their job exists, so an old copy cannot slip in after its row is pruned. Hangfire's storage API is
/// synchronous, and so is this: it runs on the worker's job thread.
/// </summary>
internal sealed class JobReplayLedger(NpgsqlDataSource dataSource)
{
    /// <summary>Binds the nonce to <paramref name="jobId"/> on its first run; the refusal when it is bound to another job or too old.</summary>
    public string? Claim(JobToken token, string jobId)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
            with claimed as (
                insert into platform.job_nonces (nonce, job_id, signed_at)
                select @nonce, @job_id, @signed_at
                where @signed_at >= now() - @max_age
                on conflict (nonce) do nothing
                returning job_id
            )
            select job_id from claimed
            union all
            select job_id from platform.job_nonces where nonce = @nonce
            limit 1
            """, connection);
        Bind(command, token, jobId);
        return Outcome(command.ExecuteScalar() as string, jobId);
    }

    /// <summary>The same decision without binding anything (the retry election).</summary>
    public string? Check(JobToken token, string jobId)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
            select coalesce(
                (select job_id from platform.job_nonces where nonce = @nonce),
                case when @signed_at >= now() - @max_age then @job_id end)
            """, connection);
        Bind(command, token, jobId);
        return Outcome(command.ExecuteScalar() as string, jobId);
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

    private static void Bind(NpgsqlCommand command, JobToken token, string jobId)
    {
        command.Parameters.AddWithValue("nonce", token.Nonce);
        command.Parameters.AddWithValue("job_id", jobId);
        command.Parameters.AddWithValue("signed_at", token.SignedAt);
        command.Parameters.AddWithValue("max_age", JobAuthenticity.MaxAgeAtFirstRun);
    }

    private static string? Outcome(string? boundTo, string jobId) => boundTo switch
    {
        null => $"the job's signature is older than {JobAuthenticity.MaxAgeAtFirstRun.TotalDays:0} days and it never ran",
        _ when string.Equals(boundTo, jobId, StringComparison.Ordinal) => null,
        _ => "the job's signature already ran as another job (a copied row)",
    };
}
