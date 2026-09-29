using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Npgsql;

namespace Platform.Web.Edge;

/// <summary>
/// W-24: one Data Protection key ring for every Platform.Web instance, so the login cookie survives a restart and is
/// accepted by any instance behind the edge (as are antiforgery tokens and Blazor's prerendered component state).
/// <list type="bullet">
/// <item>Store: PostgreSQL, <c>platform.data_protection_keys</c> (<see cref="PostgresXmlRepository"/>). Every instance
/// already shares that database, backups already cover it, and it needs no new package or service; Redis runs without
/// persistence in the stack and a file share would need one per deployment.</item>
/// <item>Access: whoever can add a key can forge any session, so the ring has its own role, <c>erp_key_ring</c>, and its
/// own connection string, <c>ConnectionStrings:KeyRing</c> (a secret, N-10), used by this pool alone. The application
/// role (<c>ConnectionStrings:Platform</c>) has no right on the table, and the host refuses to use it for the ring.</item>
/// <item>Application name <see cref="ApplicationName"/>: without it the key ring is isolated per content root path, so
/// two instances deployed to different folders could not read each other's cookies.</item>
/// <item>At rest: the certificate in <c>DataProtection:CertificatePath</c> (a PFX, password in
/// <c>DataProtection:CertificatePassword</c>, a secret) encrypts every key before it is stored, and keys stored without it
/// are then ignored. It is required outside Development and Testing; there the keys are stored unencrypted, and Data
/// Protection says so in a warning.</item>
/// <item>Logging: Data Protection writes whole key elements at Debug and Trace. Its categories are capped at Information
/// for every provider, and outside Development the host refuses to start if Debug is still enabled for them (N-10).</item>
/// </list>
/// Platform.Worker does not call this: it issues and reads no cookie and protects no payload. A job that one day has to
/// protect something the web host reads registers the same ring.
/// </summary>
internal static class KeyRing
{
    public const string ApplicationName = "waslabid-web";
    public const string ConnectionStringName = "KeyRing";
    public const string CertificatePathSetting = "DataProtection:CertificatePath";
    public const string CertificatePasswordSetting = "DataProtection:CertificatePassword";
    public const string LogCategory = "Microsoft.AspNetCore.DataProtection";

    private const string DataSourceKey = "Platform.Web.KeyRing";

    public static IServiceCollection AddKeyRing(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var connectionString = KeyRingConnectionString(configuration);
        var certificate = LoadCertificate(configuration, environment);
        services.AddKeyedSingleton(DataSourceKey, (_, _) => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton(sp => new PostgresXmlRepository(
            sp.GetRequiredKeyedService<NpgsqlDataSource>(DataSourceKey), certificate is not null, sp.GetRequiredService<ILogger<PostgresXmlRepository>>()));
        services.AddOptions<KeyManagementOptions>().Configure<PostgresXmlRepository>((options, repository) => options.XmlRepository = repository);
        var builder = services.AddDataProtection().SetApplicationName(ApplicationName);
        if (certificate is not null)
        {
            builder.ProtectKeysWithCertificate(certificate);
        }

        services.PostConfigure<LoggerFilterOptions>(CapDataProtectionLogging);
        // First among the hosted services, so it runs before Data Protection loads the ring at startup.
        services.Insert(0, ServiceDescriptor.Singleton<IHostedService, KeyRingLoggingGuard>());
        return services;
    }

    /// <summary>
    /// Caps every Data Protection category at Information for every logging provider: a rule already naming such a
    /// category is raised to Information, and each provider (and the default) gets a rule for <see cref="LogCategory"/>,
    /// which is more specific than a provider's catch-all rule (an operator's "Trace for everything").
    /// </summary>
    internal static void CapDataProtectionLogging(LoggerFilterOptions options)
    {
        for (var i = 0; i < options.Rules.Count; i++)
        {
            var rule = options.Rules[i];
            if (rule.CategoryName?.StartsWith(LogCategory, StringComparison.Ordinal) == true && (rule.LogLevel is null || rule.LogLevel < LogLevel.Information))
            {
                options.Rules[i] = new LoggerFilterRule(rule.ProviderName, rule.CategoryName, LogLevel.Information, rule.Filter);
            }
        }

        foreach (var provider in options.Rules.Select(r => r.ProviderName).Append(null).Distinct().ToList())
        {
            if (!options.Rules.Any(r => r.ProviderName == provider && r.CategoryName == LogCategory))
            {
                options.Rules.Add(new LoggerFilterRule(provider, LogCategory, LogLevel.Information, null));
            }
        }
    }

    private static string KeyRingConnectionString(IConfiguration configuration)
    {
        var keyRing = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(keyRing))
        {
            throw new InvalidOperationException(
                $"Connection string '{ConnectionStringName}' is not configured. The Data Protection key ring is read and written only as "
                + "the role erp_key_ring; set it with dotnet user-secrets (docs/07 section 4).");
        }

        var ringUser = new NpgsqlConnectionStringBuilder(keyRing).Username;
        var appUser = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("Platform") ?? string.Empty).Username;
        if (string.IsNullOrEmpty(ringUser) || string.Equals(ringUser, appUser, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Connection string '{ConnectionStringName}' must connect as its own role (erp_key_ring), not as the application role.");
        }

        return keyRing;
    }

    private static X509Certificate2? LoadCertificate(IConfiguration configuration, IHostEnvironment environment)
    {
        var path = configuration[CertificatePathSetting];
        if (string.IsNullOrWhiteSpace(path))
        {
            if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
            {
                return null;
            }

            throw new InvalidOperationException(
                $"Setting '{CertificatePathSetting}' is not configured. Outside Development the Data Protection keys are encrypted "
                + "at rest with that certificate (a PFX file; its password in the secret '" + CertificatePasswordSetting + "').");
        }

        X509Certificate2 certificate;
        try
        {
            // Ephemeral: the private key stays in memory, so Windows leaves no key container behind. macOS has no
            // ephemeral key sets.
            var flags = OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;
            certificate = X509CertificateLoader.LoadPkcs12FromFile(path, configuration[CertificatePasswordSetting], flags);
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
        {
            // The type only: the message of a failed PKCS#12 load never needs the password, and this one never carries it.
            throw new InvalidOperationException(
                $"The certificate in setting '{CertificatePathSetting}' could not be loaded ({exception.GetType().Name}). "
                + $"Check the file and the secret '{CertificatePasswordSetting}'.", exception);
        }

        if (!certificate.HasPrivateKey)
        {
            certificate.Dispose();
            throw new InvalidOperationException(
                $"The certificate in setting '{CertificatePathSetting}' has no private key, so it could not decrypt the keys it protects.");
        }

        return certificate;
    }

    /// <summary>Outside Development, refuses to start while Data Protection could write key elements to a log (N-10).</summary>
    private sealed class KeyRingLoggingGuard(ILoggerFactory loggers, IHostEnvironment environment) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (!environment.IsDevelopment() && loggers.CreateLogger(LogCategory + ".KeyManagement.XmlKeyManager").IsEnabled(LogLevel.Debug))
            {
                throw new InvalidOperationException(
                    $"Logging category '{LogCategory}' is enabled below Information. Data Protection writes whole key elements at "
                    + "Debug and Trace, so the host does not start with it outside Development (N-10).");
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
