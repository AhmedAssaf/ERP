using Npgsql;
using Platform.Shared.Data;

namespace Platform.Migrator;

/// <summary>
/// Gives a role created without a login by a migration (W-24 <c>erp_key_ring</c>, W-36 <c>erp_worker</c>) its login, with
/// the password of the connection string its own host connects with, so the password never appears in a script or in the
/// repository (N-10). Run as the owner after the migrations; running it again sets the same password. The owner needs
/// CREATEROLE (or superuser) (docs/07 section 4).
/// </summary>
internal static class RoleLogin
{
    public static async Task EnableAsync(
        string ownerConnectionString, string roleConnectionString, string role, string connectionStringName, CancellationToken cancellationToken)
    {
        var builder = new NpgsqlConnectionStringBuilder(roleConnectionString);
        if (!string.Equals(builder.Username, role, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Connection string '{connectionStringName}' must connect as the role {role}, the only role with its rights.");
        }

        if (string.IsNullOrEmpty(builder.Password))
        {
            throw new InvalidOperationException($"Connection string '{connectionStringName}' has no password.");
        }

        // Only a SCRAM-SHA-256 verifier computed here reaches the server, never the password (N-10): the statement text can
        // land in the server log (log_statement ddl or all, or log_min_error_statement when the ALTER fails). The verifier
        // is base64 and fixed punctuation only, so it is safe as a literal; ALTER ROLE takes no parameters. The role name
        // is one of this assembly's constants.
        var verifier = ScramSha256.Verifier(builder.Password);
        if (verifier.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=' or '$' or ':' or '-')))
        {
            throw new InvalidOperationException("The SCRAM verifier has an unexpected character.");
        }

        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
#pragma warning disable CA2100 // A constant role name and a verifier checked above to hold only base64 and fixed punctuation.
        await using var alter = new NpgsqlCommand($"alter role {role} login password '{verifier}'", connection);
#pragma warning restore CA2100
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }
}
