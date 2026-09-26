using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.Modules.Tenancy.Contracts;

namespace Platform.Modules.Tenancy;

internal sealed class TenantCatalog([FromKeyedServices(TenancyModule.DataSourceKey)] NpgsqlDataSource dataSource) : ITenantCatalog
{
    public async Task<IReadOnlyList<TenantSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            "select tenant_id, slug, keycloak_org_alias, portal_name, status from tenancy.list_tenants()");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var tenants = new List<TenantSummary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            tenants.Add(new TenantSummary(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        }

        return tenants;
    }
}
