using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.Modules.Audit;
using Platform.Modules.Workflow;
using Platform.Modules.Workflow.Contracts;
using Platform.Shared;
using Platform.Shared.Tenancy;

namespace Platform.Migrator;

public sealed record DevTenant(Guid Id, string Slug, string OrgAlias, string Culture, string PortalName, string PrimaryColor, string Host)
{
    public TenantContext ToContext() => new(Id, Slug, OrgAlias, Culture, new TenantBranding(PortalName, PrimaryColor, null));
}

/// <summary>Development and test data only. Tenant provisioning for real customers is F-01.</summary>
public static class DevSeed
{
    public static DevTenant Acme { get; } = new(
        Guid.Parse("0f0e0d0c-0000-7000-8000-00000000ac01"), "acme", "acme", "ar-SA", "Acme Contracting", "#0F766E", "acme.localhost");

    public static DevTenant Beta { get; } = new(
        Guid.Parse("0f0e0d0c-0000-7000-8000-00000000be01"), "beta", "beta", "en-US", "Beta Industries", "#9A3412", "beta.localhost");

    public static IReadOnlyList<DevTenant> Tenants { get; } = [Acme, Beta];

    public static async Task SeedTenantsAsync(string ownerConnectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (var tenant in Tenants)
        {
            await using var command = new NpgsqlCommand("""
                insert into tenancy.tenants (id, slug, keycloak_org_alias, default_culture, portal_name, primary_color)
                values (@id, @slug, @alias, @culture, @portal, @color)
                on conflict do nothing;
                insert into tenancy.tenant_hosts (host, tenant_id) values (@host, @id) on conflict do nothing;
                """, connection);
            command.Parameters.AddWithValue("id", tenant.Id);
            command.Parameters.AddWithValue("slug", tenant.Slug);
            command.Parameters.AddWithValue("alias", tenant.OrgAlias);
            command.Parameters.AddWithValue("culture", tenant.Culture);
            command.Parameters.AddWithValue("portal", tenant.PortalName);
            command.Parameters.AddWithValue("color", tenant.PrimaryColor);
            command.Parameters.AddWithValue("host", tenant.Host);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>Gives each dev tenant the default approval chain, through the module's own service as the app role.</summary>
    public static async Task SeedWorkflowsAsync(string appConnectionString, CancellationToken cancellationToken = default)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPlatformShared();
        services.AddAuditModule(appConnectionString);
        services.AddWorkflowModule(appConnectionString);
        await using var provider = services.BuildServiceProvider();

        foreach (var tenant in Tenants)
        {
            await using var scope = provider.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantAccessor>().Set(tenant.ToContext());
            var definitions = scope.ServiceProvider.GetRequiredService<IWorkflowDefinitions>();
            if (await definitions.FindDefaultAsync(cancellationToken) is not null)
            {
                continue;
            }

            var saved = await definitions.SaveAsync(new SaveDefinition(null, DefaultTemplate.Name, true, DefaultTemplate.Steps), cancellationToken);
            if (!saved.IsSuccess)
            {
                throw new InvalidOperationException($"Seeding the default chain for {tenant.Slug} failed: {saved.Error.Message}");
            }
        }
    }
}
