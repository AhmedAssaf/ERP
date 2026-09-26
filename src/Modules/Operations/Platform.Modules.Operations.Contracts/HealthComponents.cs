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

    /// <summary>The board's tiles, in display order.</summary>
    public static IReadOnlyList<string> Board { get; } = [Web, Worker, PostgreSql, ObjectStorage, Keycloak, ClamAv, Email];

    /// <summary>A result older than this is shown as Unknown (spec 3.2).</summary>
    public static TimeSpan StaleAfter { get; } = TimeSpan.FromSeconds(120);
}
