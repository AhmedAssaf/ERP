namespace Platform.Modules.Operations.Contracts;

/// <summary>The component names health results are recorded under, and the seven the F-51 board shows (docs/05 row 17).</summary>
public static class HealthComponents
{
    public const string Web = "Web";
    public const string Worker = "Worker";
    public const string PostgreSql = "PostgreSQL";
    public const string ObjectStorage = "MinIO";
    public const string Keycloak = "Keycloak";
    public const string ClamAv = "ClamAV";
    public const string Email = "SMTP";

    /// <summary>Checked for alerts (F-60) but not a board tile.</summary>
    public const string Disk = "Disk";

    /// <summary>
    /// The telemetry pipeline (W-10, O-14): the OpenTelemetry Collector's health and Elasticsearch's cluster health. Checked
    /// for alerts (F-60) but not a board tile, like <see cref="Disk"/>; only on hosts where both health URLs are configured.
    /// </summary>
    public const string Telemetry = "Telemetry";

    /// <summary>
    /// Redis (W-34): the shared duplicate-CR throttle's store. Checked for alerts (F-60) but not a board tile, like
    /// <see cref="Disk"/> and <see cref="Telemetry"/>; only on hosts where <c>ConnectionStrings:Redis</c> is configured.
    /// </summary>
    public const string Redis = "Redis";

    /// <summary>
    /// The worker's recurring jobs (W-42): Unhealthy when the worker's recurring job guard found an entry missing or altered
    /// and restored it, Healthy on its next pass with every entry intact. Checked for alerts (F-60) but not a board tile,
    /// like <see cref="Disk"/>; recorded every five minutes by the guard itself, not by the health-check job, so a deleted
    /// health-check entry is reported too.
    /// </summary>
    public const string Jobs = "Jobs";

    /// <summary>The board's tiles, in display order.</summary>
    public static IReadOnlyList<string> Board { get; } = [Web, Worker, PostgreSql, ObjectStorage, Keycloak, ClamAv, Email];

    /// <summary>A result older than this is shown as Unknown (spec 3.2).</summary>
    public static TimeSpan StaleAfter { get; } = TimeSpan.FromSeconds(120);
}
