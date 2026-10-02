using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>
/// Secrets the hosts require, generated when the test run starts and never written anywhere (N-10). Every web factory and
/// module host of the run uses the same values, so a test can compute what the host stored with them.
/// </summary>
internal static class TestSecrets
{
    /// <summary>The key of the duplicate-CR audit (V-6), <c>Vendors:CrAuditKey</c>: 32 random bytes.</summary>
    public static byte[] CrAuditKey { get; } = RandomNumberGenerator.GetBytes(32);

    /// <summary>The setting's name and value as the hosts read them.</summary>
    public static KeyValuePair<string, string?> CrAuditKeySetting { get; } = new("Vendors:CrAuditKey", Convert.ToBase64String(CrAuditKey));

    /// <summary>The HMAC-SHA256 of the CR number under the test key, in lower-case hex, as the audit stores it.</summary>
    public static string CrAuditHmac(string crNumber) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(CrAuditKey, Encoding.UTF8.GetBytes(crNumber)));

    /// <summary>The password of <c>erp_key_ring</c> (W-24) for this run; the database fixture gives the role its login with it.</summary>
    public static string KeyRingPassword { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    /// <summary>
    /// <c>ConnectionStrings:KeyRing</c> for the database of <paramref name="connectionString"/>: the same server and database,
    /// as the key ring's own role.
    /// </summary>
    public static string KeyRingConnectionString(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Username = "erp_key_ring", Password = KeyRingPassword }.ConnectionString;

    /// <summary>The password of <c>erp_worker</c> (W-36) for this run; the database fixture gives the role its login with it.</summary>
    public static string WorkerPassword { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    /// <summary>
    /// <c>ConnectionStrings:Worker</c> for the database of <paramref name="connectionString"/>: the same server and database,
    /// as the worker's own role.
    /// </summary>
    public static string WorkerConnectionString(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Username = "erp_worker", Password = WorkerPassword }.ConnectionString;
}
