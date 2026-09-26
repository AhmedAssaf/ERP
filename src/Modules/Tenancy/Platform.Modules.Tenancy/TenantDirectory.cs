using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.Modules.Tenancy.Contracts;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Tenancy;

internal sealed class TenantDirectory(
    [FromKeyedServices(TenancyModule.DataSourceKey)] NpgsqlDataSource dataSource,
    IMemoryCache cache) : ITenantDirectory
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    public async Task<TenantContext?> FindByHostAsync(string host, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
#pragma warning disable CA1308 // Host names compare in lower case (RFC 4343); the table stores them lower-cased.
        var normalized = host.ToLowerInvariant();
#pragma warning restore CA1308
        var key = "tenant-host:" + normalized;
        if (cache.TryGetValue(key, out TenantContext? cached))
        {
            return cached;
        }

        await using var command = dataSource.CreateCommand(
            "select tenant_id, slug, keycloak_org_alias, default_culture, portal_name, primary_color, logo_url from tenancy.resolve_host($1)");
        command.Parameters.Add(new NpgsqlParameter { Value = normalized });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        TenantContext? tenant = null;
        if (await reader.ReadAsync(cancellationToken))
        {
            tenant = new TenantContext(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                new TenantBranding(reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        cache.Set(key, tenant, CacheFor);
        return tenant;
    }
}
