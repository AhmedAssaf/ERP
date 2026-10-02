using System.Data.Common;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Npgsql;
using Platform.Shared.Data;

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
/// role (<c>ConnectionStrings:Platform</c>) has no right on the table, and the host accepts no other role for the ring.</item>
/// <item>Application name <see cref="ApplicationName"/>: without it the key ring is isolated per content root path, so
/// two instances deployed to different folders could not read each other's cookies.</item>
/// <item>At rest: the certificate in <c>DataProtection:CertificatePath</c> (a PFX, password in
/// <c>DataProtection:CertificatePassword</c>, a secret) encrypts every key before it is stored, and keys stored without it
/// are then ignored. It is required outside Development and Testing; there the keys are stored unencrypted, and Data
/// Protection says so in a warning.</item>
/// <item>Availability: a key ring that cannot be read stops the host at startup when the cause never heals by itself, and
/// makes <c>/health</c> Unhealthy otherwise (<see cref="KeyRingAvailability"/>).</item>
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
    public const string KeyRingRoleName = "erp_key_ring";

    public const string DataSourceKey = "Platform.Web.KeyRing";

    /// <summary>
    /// The ring's own pool: Data Protection reads it rarely and /health at most once per cache window, so a few connections
    /// are plenty, and every pool together must stay below PostgreSQL max_connections (as in the Tenancy module).
    /// </summary>
    public const int DefaultMaxPoolSize = 3;

    private static readonly string[] PoolSizeKeywords = ["Maximum Pool Size", "Max Pool Size", "MaxPoolSize"];

    public static IServiceCollection AddKeyRing(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var connectionString = KeyRingConnectionString(configuration);
        var certificate = LoadCertificate(configuration, environment);
        var pooled = new NpgsqlConnectionStringBuilder(connectionString);
        if (!PoolSizeKeywords.Any(new DbConnectionStringBuilder { ConnectionString = connectionString }.ContainsKey))
        {
            pooled.MaxPoolSize = DefaultMaxPoolSize;
        }

        // Named (W-10): an unnamed data source is named after its connection string in metrics and spans.
        services.AddKeyedSingleton(DataSourceKey, (_, _) => new NpgsqlDataSourceBuilder(pooled.ConnectionString) { Name = DataSourceNames.KeyRing }.Build());
        services.AddSingleton<IKeyRingCheck, DatabaseKeyRingCheck>();
        services.AddSingleton<KeyRingProbe>();
        services.AddSingleton(sp => new PostgresXmlRepository(
            sp.GetRequiredKeyedService<NpgsqlDataSource>(DataSourceKey), certificate, sp.GetRequiredService<ILogger<PostgresXmlRepository>>()));
        services.AddOptions<KeyManagementOptions>().Configure<PostgresXmlRepository>((options, repository) => options.XmlRepository = repository);
        var builder = services.AddDataProtection().SetApplicationName(ApplicationName);
        if (certificate is not null)
        {
            builder.ProtectKeysWithCertificate(certificate);
        }

        services.PostConfigure<LoggerFilterOptions>(CapDataProtectionLogging);
        // First among the hosted services, so they run before Data Protection loads the ring at startup.
        services.Insert(0, ServiceDescriptor.Singleton<IHostedService, KeyRingLoggingGuard>());
        services.Insert(1, ServiceDescriptor.Singleton<IHostedService, KeyRingStartupCheck>());
        // The probe gives up after three seconds on its own; this is the backstop, still inside the worker's five (F-51).
        services.AddHealthChecks().AddCheck<KeyRingHealthCheck>("key-ring", timeout: TimeSpan.FromSeconds(4));
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

        // Exactly the role the migrator gives a login (KeyRingRole): never the application role, and never a pasted owner
        // or other connection string that would carry more rights than SELECT and INSERT on the ring.
        var ringUser = new NpgsqlConnectionStringBuilder(keyRing).Username;
        if (!string.Equals(ringUser, KeyRingRoleName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Connection string '{ConnectionStringName}' must connect as its own role ({KeyRingRoleName}), not as the application role "
                + "or any other role.");
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
