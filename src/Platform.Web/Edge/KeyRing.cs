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
/// <item>Application name <see cref="ApplicationName"/>: without it the key ring is isolated per content root path, so
/// two instances deployed to different folders could not read each other's cookies.</item>
/// <item>At rest: the certificate in <c>DataProtection:CertificatePath</c> (a PFX, password in
/// <c>DataProtection:CertificatePassword</c>, a secret, N-10) encrypts every key before it is stored. It is required
/// outside Development and Testing; there the keys are stored unencrypted, and Data Protection says so in a warning.</item>
/// </list>
/// Platform.Worker does not call this: it issues and reads no cookie and protects no payload. A job that one day has to
/// protect something the web host reads registers the same ring.
/// </summary>
internal static class KeyRing
{
    public const string ApplicationName = "waslabid-web";
    public const string CertificatePathSetting = "DataProtection:CertificatePath";
    public const string CertificatePasswordSetting = "DataProtection:CertificatePassword";

    private const string DataSourceKey = "Platform.Web.KeyRing";

    public static IServiceCollection AddKeyRing(
        this IServiceCollection services, string connectionString, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var certificate = LoadCertificate(configuration, environment);
        services.AddKeyedSingleton(DataSourceKey, (_, _) => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton(sp => new PostgresXmlRepository(sp.GetRequiredKeyedService<NpgsqlDataSource>(DataSourceKey)));
        services.AddOptions<KeyManagementOptions>().Configure<PostgresXmlRepository>((options, repository) => options.XmlRepository = repository);
        var builder = services.AddDataProtection().SetApplicationName(ApplicationName);
        if (certificate is not null)
        {
            builder.ProtectKeysWithCertificate(certificate);
        }

        return services;
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
            certificate = X509CertificateLoader.LoadPkcs12FromFile(path, configuration[CertificatePasswordSetting]);
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
}
