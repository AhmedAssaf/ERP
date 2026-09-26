using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.Modules.Tenancy.Contracts;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Tenancy;

/// <summary>
/// Host-to-tenant lookup with its own bounded cache. The host name is attacker-controlled, so the cache is not the
/// shared one: a flood of unknown hosts can evict only tenancy entries, never another module's.
/// </summary>
internal sealed class TenantDirectory(
    [FromKeyedServices(TenancyModule.DataSourceKey)] NpgsqlDataSource dataSource) : ITenantDirectory, IDisposable
{
    private const int MaxHostLength = 253; // RFC 1035 section 2.3.4, textual form without the trailing dot.
    private static readonly TimeSpan FoundFor = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MissFor = TimeSpan.FromSeconds(5);

    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 10_000 });

    public async Task<TenantContext?> FindByHostAsync(string host, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(host);
        if (normalized is null)
        {
            return null;
        }

        if (_cache.TryGetValue(normalized, out TenantContext? cached))
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

        _cache.Set(normalized, tenant, new MemoryCacheEntryOptions
        {
            Size = 1,
            AbsoluteExpirationRelativeToNow = tenant is null ? MissFor : FoundFor,
        });
        return tenant;
    }

    public void Invalidate(string host)
    {
        var normalized = Normalize(host);
        if (normalized is not null)
        {
            _cache.Remove(normalized);
        }
    }

    public void Dispose() => _cache.Dispose();

    /// <summary>Lower-cased host (RFC 4343), or null when it cannot be a host name we serve.</summary>
    private static string? Normalize(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > MaxHostLength)
        {
            return null;
        }

#pragma warning disable CA1308 // Host names compare in lower case (RFC 4343); the table stores them lower-cased.
        var normalized = host.ToLowerInvariant();
#pragma warning restore CA1308
        foreach (var c in normalized)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-'))
            {
                return null;
            }
        }

        return normalized;
    }
}
