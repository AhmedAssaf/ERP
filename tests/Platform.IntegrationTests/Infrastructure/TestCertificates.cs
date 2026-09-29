using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>
/// A self-signed RSA certificate that encrypts the Data Protection key ring at rest (W-24, <c>DataProtection:*</c>),
/// generated when the test run starts and written as a password-protected PFX to a file of its own in the temporary
/// folder. The password is random per run and never written anywhere (N-10).
/// </summary>
internal static class TestCertificates
{
    private static readonly Lazy<(string Path, string Password)> KeyRing = new(Create);

    /// <summary>The PFX file, as <c>DataProtection:CertificatePath</c> names it.</summary>
    public static string KeyRingPath => KeyRing.Value.Path;

    /// <summary>The PFX password, as <c>DataProtection:CertificatePassword</c> carries it.</summary>
    public static string KeyRingPassword => KeyRing.Value.Password;

    private static (string Path, string Password) Create()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=waslabid-tests-key-ring", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DataEncipherment, critical: true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var path = Path.Combine(Path.GetTempPath(), $"waslabid-key-ring-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, password));
        return (path, password);
    }
}
