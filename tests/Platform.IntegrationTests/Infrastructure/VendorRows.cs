using System.Globalization;
using System.Security.Cryptography;
using Npgsql;
using Platform.Shared.Tenancy;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>A vendor user's row as the owner reads it, bypassing row-level security.</summary>
internal sealed record VendorUserRow(
    Guid CompanyId, string Role, string PrivacyNoticeVersion, DateTimeOffset PrivacyAcceptedAt, string? PrivacyNoticeCulture);

/// <summary>
/// Vendor rows for tests (vendor slice): registration through <c>vendor.register_company</c> as the app role with the
/// tenant and the acting user set, as a tenant-host request would, and reads as the owner so assertions see past row-level security.
/// </summary>
internal static class VendorRows
{
    /// <summary>Ten digits, random enough that tests sharing the database never collide.</summary>
    public static string NewCrNumber() =>
        RandomNumberGenerator.GetInt32(1_000_000_000, int.MaxValue).ToString(CultureInfo.InvariantCulture);

    /// <summary>Registers a company through the tenant's host for <paramref name="userId"/> and returns its id.</summary>
    public static async Task<Guid> RegisterAsync(
        string appConnectionString, TenantContext tenant, string userId, string crNumber, string nameEn, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(appConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            select set_config('app.tenant_id', @tenant, false), set_config('app.user_id', @user, false);
            select vendor.register_company(@cr, 'شركة الاختبار', @name, '300000000000003', 'Riyadh',
                                           'Contact Person', '+966500000000', 'contact@example.test', 'V1', 'en-US');
            """, connection);
        command.Parameters.AddWithValue("tenant", tenant.TenantId.ToString("D", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("cr", crNumber);
        command.Parameters.AddWithValue("name", nameEn);
        command.Parameters.AddWithValue("user", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.NextResultAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return reader.GetGuid(0);
    }

    /// <summary>A pending relationship between the company and another tenant, as joining it will create (task 5).</summary>
    public static async Task RelateAsync(string ownerConnectionString, Guid tenantId, Guid companyId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "insert into vendor.relationships (tenant_id, company_id, status) values (@tenant, @company, 'pending')", connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("company", companyId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task<VendorUserRow?> FindUserAsync(string ownerConnectionString, string userId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "select company_id, role, privacy_notice_version, privacy_accepted_at, privacy_notice_culture from vendor.vendor_users where user_id = @user", connection);
        command.Parameters.AddWithValue("user", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new VendorUserRow(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3),
                reader.IsDBNull(4) ? null : reader.GetString(4))
            : null;
    }

    /// <summary>The relationship statuses of the company by tenant.</summary>
    public static async Task<IReadOnlyDictionary<Guid, string>> RelationshipsAsync(
        string ownerConnectionString, Guid companyId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "select tenant_id, status from vendor.relationships where company_id = @company", connection);
        command.Parameters.AddWithValue("company", companyId);
        var result = new Dictionary<Guid, string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result[reader.GetGuid(0)] = reader.GetString(1);
        }

        return result;
    }

    public static async Task<int> CompaniesWithCrAsync(string ownerConnectionString, string crNumber, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("select count(*)::int from vendor.companies where cr_number = @cr", connection);
        command.Parameters.AddWithValue("cr", crNumber);
        return (int)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>The data of the tenant's audit events with <paramref name="action"/> by <paramref name="actorId"/>, newest first.</summary>
    public static async Task<IReadOnlyList<(string? SubjectId, string Data)>> AuditsAsync(
        string ownerConnectionString, Guid tenantId, string actorId, string action, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            select subject_id, coalesce(data::text, '{}') from audit.events
            where tenant_id = @tenant and actor_id = @actor and action = @action
            order by occurred_at desc
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("actor", actorId);
        command.Parameters.AddWithValue("action", action);
        var result = new List<(string?, string)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add((reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetString(1)));
        }

        return result;
    }

    /// <summary>The platform audit rows (<c>ops.platform_audit</c>) with <paramref name="action"/> by <paramref name="actorId"/>, newest first.</summary>
    public static async Task<IReadOnlyList<(string SubjectType, string? SubjectId, string Data)>> PlatformAuditsAsync(
        string ownerConnectionString, string actorId, string action, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            select subject_type, subject_id, data::text from ops.platform_audit
            where actor_id = @actor and action = @action
            order by occurred_at desc
            """, connection);
        command.Parameters.AddWithValue("actor", actorId);
        command.Parameters.AddWithValue("action", action);
        var result = new List<(string, string?, string)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2)));
        }

        return result;
    }
}
