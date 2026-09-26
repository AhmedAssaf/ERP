using Microsoft.Extensions.Configuration;

namespace Platform.Modules.Operations.Health;

/// <summary>
/// Configuration for the checks (spec 3.2, plan task 3). MinIO's access key and secret never have a default: they
/// come from user secrets or the environment only (N-10), never from an appsettings file.
/// </summary>
internal sealed record HealthCheckSettings(
    string PostgreSqlConnectionString,
    string MinIoServiceUrl,
    string MinIoBucketName,
    string? MinIoAccessKey,
    string? MinIoSecretKey,
    string KeycloakManagementUrl,
    string ClamAvHost,
    int ClamAvPort,
    string SmtpHost,
    int SmtpPort,
    string WebHealthUrl)
{
    public static HealthCheckSettings FromConfiguration(IConfiguration configuration, string postgreSqlConnectionString)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(postgreSqlConnectionString);

        return new HealthCheckSettings(
            postgreSqlConnectionString,
            configuration["Health:MinIo:ServiceUrl"] ?? "http://localhost:9002",
            configuration["Health:MinIo:BucketName"] ?? "erp-dev",
            configuration["Health:MinIo:AccessKey"],
            configuration["Health:MinIo:SecretKey"],
            configuration["Keycloak:ManagementUrl"] ?? "http://localhost:9000",
            configuration["ClamAv:Host"] ?? "localhost",
            ParseInt(configuration["ClamAv:Port"], 3310),
            configuration["Smtp:Host"] ?? "localhost",
            ParseInt(configuration["Smtp:Port"], 1025),
            configuration["Platform:WebHealthUrl"] ?? "http://localhost:5273/health");
    }

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, out var parsed) ? parsed : fallback;
}
