using System.Collections.Concurrent;
using System.Linq.Expressions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;

namespace Platform.Shared.Jobs;

/// <summary>One recurring job as the worker defines it: id, job and cron, in UTC on the default queue unless the job names one.</summary>
public sealed record RecurringJobDefinition(string Id, Job Job, string Cron)
{
    public string Queue => Job.Queue ?? EnqueuedState.DefaultQueue;
}

/// <summary>
/// W-42: the worker's recurring jobs, as the modules schedule them (<c>Schedule*Jobs</c>). Each is written through the signed
/// recurring job manager and remembered here, so <see cref="RecurringJobGuard"/> can find an entry that went missing or was
/// altered and write it back. Only the worker's role may write the recurring entries (jobs migration 0001); the
/// application role reads them.
/// </summary>
public sealed class RecurringJobCatalog(JobStorage storage, RecurringJobManager manager)
{
    /// <summary>Hangfire's set of recurring job ids, scored by the next execution.</summary>
    public const string RecurringJobsSet = "recurring-jobs";

    private readonly ConcurrentDictionary<string, RecurringJobDefinition> _definitions = new(StringComparer.Ordinal);

    public IReadOnlyCollection<RecurringJobDefinition> Definitions => [.. _definitions.Values.OrderBy(d => d.Id, StringComparer.Ordinal)];

    /// <summary>Schedules <paramref name="method"/> as <paramref name="id"/> on <paramref name="cron"/> (UTC) and remembers it.</summary>
    public void AddOrUpdate<T>(string id, Expression<Func<T, Task>> method, string cron)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(cron);
        var definition = new RecurringJobDefinition(id, Job.FromExpression(method), cron);
        _definitions[id] = definition;
        Write(definition);
    }

    /// <summary>The definitions whose stored entry is missing, out of the recurring set, or differs in job, cron, queue or time zone.</summary>
    public IReadOnlyList<RecurringJobDefinition> Drifted()
    {
        var definitions = Definitions;
        if (definitions.Count == 0)
        {
            return [];
        }

        using var connection = storage.GetConnection();
        var scheduled = connection.GetAllItemsFromSet(RecurringJobsSet);
        var stored = connection.GetRecurringJobs([.. definitions.Select(d => d.Id)]).ToDictionary(j => j.Id, StringComparer.Ordinal);
        return [.. definitions.Where(d => !scheduled.Contains(d.Id) || !stored.TryGetValue(d.Id, out var entry) || !Matches(d, entry))];
    }

    /// <summary>
    /// Ids in the recurring set that no definition names, each with whether its stored job is one no worker may run: a class
    /// this build loads that is not a platform job, or a type the allow-list refuses (<see cref="JobAllowList"/>). Only those
    /// are removed (fix round 2). An entry naming a platform job this worker does not schedule, or a type this build does not
    /// know (a newer worker's job during a deploy overlap), is left alone, so two versions never undo each other. Empty while
    /// the catalog is empty, so a host that schedules nothing looks at nothing.
    /// </summary>
    public IReadOnlyList<(string Id, bool Disallowed)> Unknown()
    {
        var known = Definitions.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        if (known.Count == 0)
        {
            return [];
        }

        using var connection = storage.GetConnection();
        var ids = connection.GetAllItemsFromSet(RecurringJobsSet).Where(id => !known.Contains(id)).Order(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var stored = connection.GetRecurringJobs(ids).ToDictionary(j => j.Id, StringComparer.Ordinal);
        return [.. ids.Select(id => (id, stored.TryGetValue(id, out var entry) && IsDisallowed(entry)))];
    }

    /// <summary>Removes a recurring entry the worker does not define (hash and set entry).</summary>
    public void Remove(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        manager.RemoveIfExists(id);
    }

    /// <summary>Writes the definition back from scratch: removed first, so a missing set entry or a changed field is restored too.</summary>
    public void Restore(RecurringJobDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        manager.RemoveIfExists(definition.Id);
        Write(definition);
    }

    private void Write(RecurringJobDefinition definition) =>
        manager.AddOrUpdate(definition.Id, definition.Job, definition.Cron, new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

    /// <summary>A refused type (the resolver threw <see cref="JobRefusedException"/>), or a loaded class the allow-list refuses.</summary>
    private static bool IsDisallowed(RecurringJobDto entry)
    {
        if (entry.Job is { } job)
        {
            return JobAllowList.Refusal(job, tenantParameter: null) is not null;
        }

        for (var exception = (Exception?)entry.LoadException; exception is not null; exception = exception.InnerException)
        {
            if (exception is JobRefusedException)
            {
                return true;
            }
        }

        return false;
    }

    private static bool Matches(RecurringJobDefinition definition, RecurringJobDto entry) =>
        !entry.Removed
        && entry.LoadException is null
        && entry.Job is { } job
        && job.Type == definition.Job.Type
        && job.Method == definition.Job.Method
        && string.Equals(InvocationData.SerializeJob(job).Arguments, InvocationData.SerializeJob(definition.Job).Arguments, StringComparison.Ordinal)
        && string.Equals(entry.Cron, definition.Cron, StringComparison.Ordinal)
        && string.Equals(entry.Queue ?? EnqueuedState.DefaultQueue, definition.Queue, StringComparison.Ordinal)
        && string.Equals(entry.TimeZoneId ?? TimeZoneInfo.Utc.Id, TimeZoneInfo.Utc.Id, StringComparison.Ordinal);
}
