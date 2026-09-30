using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Identity.Members;

namespace Platform.Modules.Identity.Activity;

/// <summary>
/// Reads <c>identity.activity_counts</c> and calls <c>identity.prune_activity</c> (spec 6.4) through the scope's connection;
/// the functions refuse a session with a tenant or a vendor context, so only the worker's job gets an answer.
/// </summary>
internal sealed class UserActivityCounts(IDbContextFactory<MembersDbContext> contexts) : IUserActivityCounts
{
    public async Task<IReadOnlyList<ActivityCount>> CountAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await db.Database.OpenConnectionAsync(cancellationToken);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "select tenant_id, kind, time_window, users from identity.activity_counts(@now)";
        Add(command, "now", now);

        var counts = new List<ActivityCount>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            counts.Add(new ActivityCount(
                await reader.IsDBNullAsync(0, cancellationToken) ? null : reader.GetGuid(0),
                reader.GetString(1) == "vendor" ? ActivityKind.Vendor : ActivityKind.Staff,
                reader.GetString(2) switch
                {
                    "1d" => ActivityWindow.OneDay,
                    "7d" => ActivityWindow.SevenDays,
                    "30d" => ActivityWindow.ThirtyDays,
                    var other => throw new InvalidOperationException($"Unknown activity window '{other}'."),
                },
                reader.GetInt32(3)));
        }

        return counts;
    }

    public async Task<int> PruneAsync(DateTimeOffset before, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await db.Database.OpenConnectionAsync(cancellationToken);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "select identity.prune_activity(@before)";
        Add(command, "before", before);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Add(DbCommand command, string name, DateTimeOffset value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value.ToUniversalTime();
        command.Parameters.Add(parameter);
    }
}
