using Npgsql;

namespace Platform.Migrator;

/// <summary>
/// W-24: platform migration 0007 creates <c>erp_key_ring</c>, the only role that may read or add Data Protection keys,
/// without a login. This gives it one, with the password of <c>ConnectionStrings:KeyRing</c> (the same secret the web
/// host's key-ring pool connects with), so the password never appears in a script or in the repository (N-10). Run as
/// the owner after the migrations; running it again sets the same password.
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

        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        // ALTER ROLE takes no parameters: the server quotes the password as a literal (format %L) and the statement it
        // returns is executed as is; neither is logged here.
        string statement;
        await using (var quote = new NpgsqlCommand($"select format('alter role {Name} login password %L', @password::text)", connection))
        {
            quote.Parameters.AddWithValue("password", keyRing.Password);
            statement = (string)(await quote.ExecuteScalarAsync(cancellationToken))!;
        }

#pragma warning disable CA2100 // The statement is built by the server from a quoted literal, not from concatenated input.
        await using var alter = new NpgsqlCommand(statement, connection);
#pragma warning restore CA2100
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }
}
