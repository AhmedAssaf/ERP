using Npgsql;
using Platform.Shared.Tenancy;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>A row of identity.members as the tests read it back.</summary>
internal sealed record MemberRow(string? UserId, string Email, string[] Roles, string Status, DateTimeOffset? ActivatedAt);

/// <summary>
/// Writes and reads identity.members as the app role with the tenant set, exactly as row-level security sees it, so tests
/// can arrange members before invitations exist (plan task 9). Audit rows are counted as the owner, across tenants.
/// </summary>
internal static class MemberRows
{
    public static TenantContext NewTenant()
    {
        var id = Guid.NewGuid();
        var slug = $"t{id:N}"[..12];
        return new TenantContext(id, slug, slug, "en-US", new TenantBranding($"Tenant {slug}", "#0F766E", null));
    }

    public static async Task InsertAsync(
        string appConnectionString, Guid tenantId, string? userId, string email, string[] roles, string status, CancellationToken cancellationToken)
    {
        await using var connection = await OpenForTenantAsync(appConnectionString, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            insert into identity.members (id, tenant_id, user_id, email, display_name, roles, status, invited_at, activated_at)
            values (gen_random_uuid(), @tenant, @user, @email, @name, @roles, @status, now(),
                    case when @status = 'active' then now() end)
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("user", (object?)userId ?? DBNull.Value);
        command.Parameters.AddWithValue("email", email);
        command.Parameters.AddWithValue("name", email.Split('@')[0]);
        command.Parameters.AddWithValue("roles", roles);
        command.Parameters.AddWithValue("status", status);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Overwrites a member's roles in the table directly, behind the directory's back (and its cache's).</summary>
    public static async Task OverwriteRolesAsync(
        string appConnectionString, Guid tenantId, string userId, string[] roles, CancellationToken cancellationToken)
    {
        await using var connection = await OpenForTenantAsync(appConnectionString, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("update identity.members set roles = @roles where user_id = @user", connection);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("roles", roles);
        (await command.ExecuteNonQueryAsync(cancellationToken)).ShouldBe(1);
    }

    public static async Task<MemberRow?> FindByEmailAsync(
        string appConnectionString, Guid tenantId, string email, CancellationToken cancellationToken)
    {
        await using var connection = await OpenForTenantAsync(appConnectionString, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand(
            "select user_id, email, roles, status, activated_at from identity.members where email = @email", connection);
        command.Parameters.AddWithValue("email", email);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new MemberRow(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.GetString(1),
            reader.GetFieldValue<string[]>(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4));
    }

    public static async Task<int> AuditCountAsync(
        string ownerConnectionString, Guid tenantId, string actorId, string action, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "select count(*) from audit.events where tenant_id = @tenant and actor_id = @actor and action = @action", connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("actor", actorId);
        command.Parameters.AddWithValue("action", action);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<NpgsqlConnection> OpenForTenantAsync(string appConnectionString, Guid tenantId, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(appConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var set = new NpgsqlCommand("select set_config('app.tenant_id', @tenant, false)", connection);
        set.Parameters.AddWithValue("tenant", tenantId.ToString());
        await set.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }
}
