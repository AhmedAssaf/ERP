using Npgsql;
using Platform.Shared.Data;

namespace Platform.Migrator;

/// <summary>
/// W-24: platform migration 0007 creates <c>erp_key_ring</c>, the only role that may read or add Data Protection keys,
/// without a login. This gives it one, with the password of <c>ConnectionStrings:KeyRing</c> (the same secret the web
/// host's key-ring pool connects with), so the password never appears in a script or in the repository (N-10). Run as
/// the owner after the migrations; running it again sets the same password. The owner needs CREATEROLE (or superuser)
/// for migration 0007 to create the role and for this to give it a login (docs/07 section 4).
/// </summary>
public static class KeyRingRole
{
    public const string Name = "erp_key_ring";

    public static async Task EnableLoginAsync(string ownerConnectionString, string keyRingConnectionString, CancellationToken cancellationToken = default)
    {
        var keyRing = new NpgsqlConnectionStringBuilder(keyRingConnectionString);
        if (!string.Equals(keyRing.Username, Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Connection string 'KeyRing' must connect as the role {Name}, the only role with rights on the key ring.");
        }

        if (string.IsNullOrEmpty(keyRing.Password))
        {
            throw new InvalidOperationException("Connection string 'KeyRing' has no password.");
        }

        // Only a SCRAM-SHA-256 verifier computed here reaches the server, never the password (N-10): the statement text can
        // land in the server log (log_statement ddl or all, or log_min_error_statement when the ALTER fails). The verifier
        // is base64 and fixed punctuation only, so it is safe as a literal; ALTER ROLE takes no parameters.
        var verifier = ScramSha256.Verifier(keyRing.Password);
        if (verifier.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=' or '$' or ':' or '-')))
        {
            throw new InvalidOperationException("The SCRAM verifier has an unexpected character.");
        }

        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
#pragma warning disable CA2100 // The literal is a verifier checked above to hold only base64 and fixed punctuation.
        await using var alter = new NpgsqlCommand($"alter role {Name} login password '{verifier}'", connection);
#pragma warning restore CA2100
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }
}
