using Npgsql;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>
/// The database's own date for tests that fix the application clock. The no-backdating trigger on the consent ledger
/// (vendors migration 0015) refuses a grant starting before today in Riyadh by the database's clock, so such tests
/// derive their days from it instead of pinning a year that one day lies behind the real date (W-39).
/// </summary>
internal static class DatabaseClock
{
    /// <summary>Days beyond today that a fixed test day sits, so that grants made up to 40 days before it still start in the future.</summary>
    public const int FutureOffsetDays = 60;

    /// <summary>
    /// A day <see cref="FutureOffsetDays"/> after today in Riyadh, by the trigger's own expression
    /// (<c>now() + 3 hours</c> as a UTC date, which is <c>current_date</c> shifted to Riyadh), read from the database.
    /// </summary>
    public static async Task<DateOnly> FutureRiyadhDayAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("select ((now() + interval '3 hours') at time zone 'UTC')::date", connection);
        var today = (DateOnly)(await command.ExecuteScalarAsync(cancellationToken))!;
        return today.AddDays(FutureOffsetDays);
    }

    /// <summary>The UTC instant at the given UTC time on the given day.</summary>
    public static DateTimeOffset Utc(DateOnly day, TimeOnly time) => new(day.ToDateTime(time), TimeSpan.Zero);
}
