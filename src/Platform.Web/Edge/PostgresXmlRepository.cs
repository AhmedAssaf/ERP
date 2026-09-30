using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Npgsql;

namespace Platform.Web.Edge;

/// <summary>
/// W-24: the Data Protection key ring in <c>platform.data_protection_keys</c> (shared migration 0007), read and written
/// only through the key-ring pool, which connects as <c>erp_key_ring</c>, the one role with rights on the table. It only
/// adds rows (Data Protection never changes a stored element; a revocation is a new element) and never logs an element: a
/// key's XML is key material (N-10); a log line names a key by its id only.
/// <list type="bullet">
/// <item>When the host encrypts keys with a certificate (<paramref name="certificate"/>), a key stored any other way is
/// ignored: it must carry its secret only as <c>encryptedSecret</c> with the certificate decryptor, and no
/// <c>masterKey</c> in the clear. Defence in depth behind the role; encryption is not authentication.</item>
/// <item>Two instances starting together would each make a key. A new key is stored under a transaction advisory lock
/// after a re-read, and skipped only when another instance has just stored its twin: a key activated within
/// <see cref="ActivationSkew"/> of it, valid past the propagation window, and (with a certificate) encrypted with this
/// host's own certificate, so this host can read it. Data Protection re-reads the ring after making a key, so that
/// instance then uses the stored twin. Any other key, such as one left behind by a certificate replacement that no
/// instance can decrypt any more, never stops a new key from being stored.</item>
/// </list>
/// </summary>
internal sealed partial class PostgresXmlRepository(NpgsqlDataSource dataSource, X509Certificate2? certificate, ILogger<PostgresXmlRepository> logger)
    : IXmlRepository
{
    // "WASLKR": this table's own advisory lock, distinct from the migrator's.
    private const long StoreLockKey = 0x5741534C4B52;

    // Data Protection makes a new key when the default key expires within this window (KeyManagementOptions default).
    private static readonly TimeSpan PropagationWindow = TimeSpan.FromDays(2);
    private static readonly TimeSpan ActivationSkew = TimeSpan.FromMinutes(5);
    private static readonly string CertificateDecryptor = typeof(EncryptedXmlDecryptor).FullName!;

    // EncryptedXml puts the encrypting certificate in the key's KeyInfo (X509Data/X509Certificate, base64 of its DER form).
    private readonly string? _certificateData = certificate is null ? null : Convert.ToBase64String(certificate.RawData);

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var connection = dataSource.OpenConnection();
        return Trusted(ReadAll(connection, null)).ToList();
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);
        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var lockCommand = new NpgsqlCommand("select pg_advisory_xact_lock(@key)", connection, transaction))
        {
            lockCommand.Parameters.AddWithValue("key", StoreLockKey);
            lockCommand.ExecuteNonQuery();
        }

        if (element.Name.LocalName == "key" && IsCoveredByStoredKey(element, Trusted(ReadAll(connection, transaction)).ToList()))
        {
            LogKeyNotStored(logger, element.Attribute("id")?.Value);
            transaction.Commit();
            return;
        }

        using var insert = new NpgsqlCommand(
            "insert into platform.data_protection_keys (friendly_name, xml) values (@friendlyName, @xml)", connection, transaction);
        insert.Parameters.AddWithValue("friendlyName", friendlyName ?? string.Empty);
        insert.Parameters.AddWithValue("xml", element.ToString(SaveOptions.DisableFormatting));
        insert.ExecuteNonQuery();
        transaction.Commit();
    }

    private static List<XElement> ReadAll(NpgsqlConnection connection, NpgsqlTransaction? transaction)
    {
        using var command = new NpgsqlCommand("select xml from platform.data_protection_keys order by id", connection, transaction);
        using var reader = command.ExecuteReader();
        var elements = new List<XElement>();
        while (reader.Read())
        {
            elements.Add(XElement.Parse(reader.GetString(0)));
        }

        return elements;
    }

    private IEnumerable<XElement> Trusted(IEnumerable<XElement> elements)
    {
        foreach (var element in elements)
        {
            if (certificate is not null && element.Name.LocalName == "key" && !IsEncryptedWithCertificate(element))
            {
                LogKeyIgnored(logger, element.Attribute("id")?.Value);
                continue;
            }

            yield return element;
        }
    }

    private static bool IsEncryptedWithCertificate(XElement key)
    {
        var descendants = key.Descendants().ToList();
        var secrets = descendants.Where(e => e.Name.LocalName == "encryptedSecret").ToList();
        return secrets.Count > 0
            && secrets.All(s => DecryptorTypeName(s) == CertificateDecryptor)
            && !descendants.Any(e => e.Name.LocalName == "masterKey");
    }

    // "Namespace.Type, Assembly, Version=..." -> "Namespace.Type".
    private static string? DecryptorTypeName(XElement secret) =>
        ((string?)secret.Attribute("decryptorType"))?.Split(',')[0].Trim();

    private bool IsCoveredByStoredKey(XElement newKey, List<XElement> stored)
    {
        // A revocation can make any stored key unusable; then storing is always right.
        if (stored.Any(e => e.Name.LocalName == "revocation") || Dates(newKey) is not { } wanted)
        {
            return false;
        }

        return stored
            .Where(e => e.Name.LocalName == "key" && IsReadableHere(e))
            .Select(Dates)
            .Any(d => d is { } have
                && (have.Activation - wanted.Activation).Duration() <= ActivationSkew
                && have.Expiration > wanted.Activation + PropagationWindow);
    }

    // Without a certificate every stored key is readable; with one, only a key encrypted with this very certificate.
    private bool IsReadableHere(XElement key) =>
        _certificateData is null
        || key.Descendants().Any(e => e.Name.LocalName == "X509Certificate" && string.Equals(
            string.Concat(e.Value.Where(c => !char.IsWhiteSpace(c))), _certificateData, StringComparison.Ordinal));

    private static (DateTimeOffset Activation, DateTimeOffset Expiration)? Dates(XElement key)
    {
        var activation = key.Element(key.Name.Namespace + "activationDate")?.Value;
        var expiration = key.Element(key.Name.Namespace + "expirationDate")?.Value;
        return DateTimeOffset.TryParse(activation, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var a)
            && DateTimeOffset.TryParse(expiration, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var e)
            ? (a, e)
            : null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Key {KeyId} was not stored: another instance stored a key for the same period first.")]
    private static partial void LogKeyNotStored(ILogger logger, string? keyId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Key {KeyId} is ignored: it is not stored encrypted with the configured certificate.")]
    private static partial void LogKeyIgnored(ILogger logger, string? keyId);
}
