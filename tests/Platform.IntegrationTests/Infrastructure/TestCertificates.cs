using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>
/// Self-signed RSA certificates that encrypt the Data Protection key ring at rest (W-24, <c>DataProtection:*</c>),
/// generated during the run and written as password-protected PFX files of their own in the temporary folder. The
/// passwords are random and never written anywhere (N-10); the files are deleted when the test process exits. The keys
/// are made in memory (CNG ephemeral on Windows), so no key container is left behind either.
/// </summary>
internal static class TestCertificates
{
    private static readonly Lazy<(string Path, string Password)> KeyRing = new(() => Create("CN=waslabid-tests-key-ring"));
    private static readonly ConcurrentBag<string> Files = [];

    static TestCertificates() => AppDomain.CurrentDomain.ProcessExit += (_, _) => DeleteFiles();

    /// <summary>The PFX file, as <c>DataProtection:CertificatePath</c> names it.</summary>
    public static string KeyRingPath => KeyRing.Value.Path;

    /// <summary>The PFX password, as <c>DataProtection:CertificatePassword</c> carries it.</summary>
    public static string KeyRingPassword => KeyRing.Value.Password;

    /// <summary>Another certificate (a rotation, a stranger's), deleted with the others when the run ends.</summary>
    public static (string Path, string Password) Create(string subject)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DataEncipherment, critical: true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var path = Path.Combine(Path.GetTempPath(), $"waslabid-key-ring-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, password));
        Files.Add(path);
        return (path, password);
    }

    private static void DeleteFiles()
    {
        foreach (var path in Files)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException exception)
            {
                Console.Error.WriteLine($"Test certificate file {path} was not deleted: {exception.GetType().Name}.");
            }
            catch (UnauthorizedAccessException exception)
            {
                Console.Error.WriteLine($"Test certificate file {path} was not deleted: {exception.GetType().Name}.");
            }
        }
    }
}
