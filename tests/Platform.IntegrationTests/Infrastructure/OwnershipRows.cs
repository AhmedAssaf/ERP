using System.Globalization;
using Npgsql;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>A company's ownership verification as the owner reads it (W-33).</summary>
internal sealed record OwnershipVerificationRow(
    string Method, string RegistrantUserId, string VerifiedBy, Guid? VerifiedInTenant, string Note);

/// <summary>A dispute as the owner reads it (W-33).</summary>
internal sealed record CrDisputeRow(
    Guid CompanyId, string ClaimantUserId, string ClaimantEmail, string ClaimantName, string Status, Guid RaisedOnTenant,
    string? ResolvedBy, string? ResolutionNote, string[]? RemovedUserIds);

/// <summary>
/// Ownership check rows for tests (W-33): the platform's method, verifications and disputes, read and written as the
/// owner past row-level security.
/// </summary>
internal static class OwnershipRows
{
    /// <summary>
    /// Records the company's ownership as verified by an officer of <paramref name="tenantId"/>, as the owner: for tests
    /// of other behaviour that approve a company and need no ownership check of their own.
    /// </summary>
    public static async Task VerifyAsOwnerAsync(string ownerConnectionString, Guid companyId, Guid tenantId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            insert into vendor.ownership_verifications (company_id, method, registrant_user_id, verified_by, verified_in_tenant, note)
            values (@company, 'manual', coalesce(vendor.company_registrant(@company), 'unknown'), 'test-officer', @tenant,
                    'Verified by the test set-up.')
            on conflict (company_id) do nothing
            """, connection);
        command.Parameters.AddWithValue("company", companyId);
        command.Parameters.AddWithValue("tenant", tenantId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task<OwnershipVerificationRow?> VerificationAsync(string ownerConnectionString, Guid companyId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            select method, registrant_user_id, verified_by, verified_in_tenant, note
            from vendor.ownership_verifications where company_id = @company
            """, connection);
        command.Parameters.AddWithValue("company", companyId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new OwnershipVerificationRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetGuid(3), reader.GetString(4))
            : null;
    }

    /// <summary>Sets the platform's method as the owner (a test's set-up; the console goes through the service).</summary>
    public static async Task SetMethodAsOwnerAsync(string ownerConnectionString, string method, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("update vendor.ownership_settings set method = @method where id", connection);
        command.Parameters.AddWithValue("method", method);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task<string> MethodAsync(string ownerConnectionString, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("select method from vendor.ownership_settings where id", connection);
        return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public static async Task<CrDisputeRow?> DisputeAsync(string ownerConnectionString, Guid disputeId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            select company_id, claimant_user_id, claimant_email, claimant_name, status, raised_on_tenant, resolved_by,
                   resolution_note, removed_user_ids
            from vendor.cr_disputes where id = @id
            """, connection);
        command.Parameters.AddWithValue("id", disputeId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new CrDisputeRow(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetGuid(5),
                reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<string[]>(8))
            : null;
    }

    /// <summary>The company's vendor users as (user id, role), oldest first.</summary>
    public static async Task<IReadOnlyList<(string UserId, string Role)>> VendorUsersAsync(
        string ownerConnectionString, Guid companyId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "select user_id, role from vendor.vendor_users where company_id = @company order by created_at, id", connection);
        command.Parameters.AddWithValue("company", companyId);
        var result = new List<(string, string)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add((reader.GetString(0), reader.GetString(1)));
        }

        return result;
    }

    /// <summary>A connection as the app role with the given session settings, as a request of that kind would have.</summary>
    public static async Task<NpgsqlConnection> AppSessionAsync(
        string appConnectionString, Guid? tenantId, Guid? vendorCompanyId, string? userId, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(appConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            select set_config('app.tenant_id', @tenant, false),
                   set_config('app.vendor_company_id', @vendor, false),
                   set_config('app.user_id', @user, false)
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId?.ToString("D", CultureInfo.InvariantCulture) ?? string.Empty);
        command.Parameters.AddWithValue("vendor", vendorCompanyId?.ToString("D", CultureInfo.InvariantCulture) ?? string.Empty);
        command.Parameters.AddWithValue("user", userId ?? string.Empty);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }
}
