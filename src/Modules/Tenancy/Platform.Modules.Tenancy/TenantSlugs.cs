using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.Modules.Tenancy.Contracts;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Tenancy;

/// <summary>
/// Slugs by id from <c>tenancy.list_tenants()</c>, for a scope with neither a tenant nor a vendor context (the worker's
/// jobs, the platform console). See <see cref="ITenantSlugs"/> for why the check is in code.
/// </summary>
internal sealed class TenantSlugs(
    [FromKeyedServices(TenancyModule.DataSourceKey)] NpgsqlDataSource dataSource, ITenantAccessor tenants, IVendorAccessor vendors) : ITenantSlugs
{
    public async Task<IReadOnlyDictionary<Guid, string>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (tenants.Current is not null || vendors.Current is not null)
        {
            throw new InvalidOperationException("Tenant slugs are listed for platform scopes only, never inside a tenant or vendor scope.");
        }

        await using var command = dataSource.CreateCommand("select tenant_id, slug from tenancy.list_tenants()");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var slugs = new Dictionary<Guid, string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            slugs[reader.GetGuid(0)] = reader.GetString(1);
        }

        return slugs;
    }
}
