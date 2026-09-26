using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.Modules.Tenancy.Contracts;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Tenancy;

/// <summary>
/// Reads <c>tenancy.list_tenants()</c>. The function is executable by <c>erp_app</c>, the role every request connects as,
/// so the database cannot tell a console request from a tenant one; like <c>resolve_host</c>, it is gated in code. This
/// class refuses unless the scope is a platform request (<see cref="IPlatformRequestContext"/>), on top of the
/// PlatformAdmin policy the console pages require.
/// </summary>
internal sealed class TenantCatalog(
    [FromKeyedServices(TenancyModule.DataSourceKey)] NpgsqlDataSource dataSource, IPlatformRequestContext platform) : ITenantCatalog
{
    public async Task<IReadOnlyList<TenantSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!platform.IsPlatform)
        {
            throw new InvalidOperationException("The tenant catalog is available to platform console requests only.");
        }

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
