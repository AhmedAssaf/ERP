using System.Security.Cryptography;
using System.Text;

namespace Platform.Shared.Data;

/// <summary>
/// A PostgreSQL SCRAM-SHA-256 password verifier (RFC 5802, RFC 7677), computed on the client as psql's <c>\password</c>
/// does, so <c>ALTER ROLE ... PASSWORD</c> carries the verifier and never the password itself (N-10): a verifier in a
/// server log does not let anyone log in. Format:
/// <c>SCRAM-SHA-256$&lt;iterations&gt;:&lt;salt&gt;$&lt;StoredKey&gt;:&lt;ServerKey&gt;</c>, base64.
/// </summary>
public static class ScramSha256
{
    /// <summary>PostgreSQL's defaults: 4096 iterations, a 16-byte random salt.</summary>
    public const int DefaultIterations = 4096;

    public static string Verifier(string password) => Verifier(password, RandomNumberGenerator.GetBytes(16), DefaultIterations);

    public static string Verifier(string password, byte[] salt, int iterations)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(salt);
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);
        // PostgreSQL normalises the password with SASLprep, which leaves printable ASCII unchanged. Outside that range the
        // two could differ, so such a password is refused rather than stored with a verifier that never matches.
        if (password.Length == 0 || password.Any(c => c is < ' ' or > '~'))
        {
            throw new ArgumentException("The password must be non-empty printable ASCII (for example from openssl rand -hex 32).", nameof(password));
        }

        var salted = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);
        var storedKey = SHA256.HashData(HMACSHA256.HashData(salted, "Client Key"u8));
        var serverKey = HMACSHA256.HashData(salted, "Server Key"u8);
        return $"SCRAM-SHA-256${iterations}:{Convert.ToBase64String(salt)}${Convert.ToBase64String(storedKey)}:{Convert.ToBase64String(serverKey)}";
    }
}
