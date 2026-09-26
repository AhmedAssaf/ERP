using Npgsql;
using Platform.Shared.Tenancy;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>
/// Throwaway tenants for tests that change a tenant's own row (F-02 branding), so the seeded acme and beta stay as the
/// other tests expect them. Each has one host, <c>{slug}.localhost</c>, and an organization alias equal to its slug.
/// </summary>
internal static class TenantRows
{
    public static async Task<TenantContext> InsertAsync(string ownerConnectionString, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var slug = $"b{id:N}"[..12];
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            insert into tenancy.tenants (id, slug, keycloak_org_alias, default_culture, portal_name, primary_color)
            values (@id, @slug, @slug, 'en-US', @name, '#0F766E');
            insert into tenancy.tenant_hosts (host, tenant_id) values (@host, @id);
            """, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("slug", slug);
        command.Parameters.AddWithValue("name", $"Tenant {slug}");
        command.Parameters.AddWithValue("host", Host(slug));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return new TenantContext(id, slug, slug, "en-US", new TenantBranding($"Tenant {slug}", "#0F766E", null));
    }

    public static string Host(string slug) => $"{slug}.localhost";

    public static string Host(TenantContext tenant) => Host(tenant.Slug);

    /// <summary>The tenant's branding as stored, through the same function the host uses.</summary>
    public static async Task<TenantBranding> BrandingAsync(string appConnectionString, TenantContext tenant, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(appConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("select portal_name, primary_color, logo_url from tenancy.resolve_host(@host)", connection);
        command.Parameters.AddWithValue("host", Host(tenant));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        (await reader.ReadAsync(cancellationToken)).ShouldBeTrue();
        return new TenantBranding(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
    }
}
