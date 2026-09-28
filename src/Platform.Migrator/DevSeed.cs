using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.Modules.Audit;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Workflow;
using Platform.Modules.Workflow.Contracts;
using Platform.Shared;
using Platform.Shared.Tenancy;

namespace Platform.Migrator;

/// <param name="AdminEmail">The realm user (infra/compose/keycloak/import/waslabid-realm.json) seeded as the tenant admin.</param>
public sealed record DevTenant(
    Guid Id, string Slug, string OrgAlias, string Culture, string PortalName, string PrimaryColor, string Host, string AdminEmail)
{
    public TenantContext ToContext() => new(Id, Slug, OrgAlias, Culture, new TenantBranding(PortalName, PrimaryColor, null));
}

/// <summary>Development and test data only. Tenant provisioning for real customers is F-01.</summary>
public static class DevSeed
{
    public static DevTenant Acme { get; } = new(
        Guid.Parse("0f0e0d0c-0000-7000-8000-00000000ac01"), "acme", "acme", "ar-SA", "Acme Contracting", "#0F766E", "acme.localhost", "admin@acme.waslabid.test");

    public static DevTenant Beta { get; } = new(
        Guid.Parse("0f0e0d0c-0000-7000-8000-00000000be01"), "beta", "beta", "en-US", "Beta Industries", "#9A3412", "beta.localhost", "admin@beta.waslabid.test");

    public static IReadOnlyList<DevTenant> Tenants { get; } = [Acme, Beta];

    /// <summary>The development seed's consent recipient (V-13): no real recipient exists yet and no migration adds one.</summary>
    public static Guid TestRecipientId { get; } = Guid.Parse("0f0e0d0c-0000-7000-8000-0000000c0e01");

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

    /// <summary>
    /// Gives each dev tenant its realm admin as a tenant admin (F-07), as the app role under row-level security. The seed
    /// does not call Keycloak, so it cannot know the user's <c>sub</c>: the row is keyed by email with no user id, and the
    /// members claims transformation binds it on that user's first sign-in with the same verified email.
    /// </summary>
    public static async Task SeedMembersAsync(string appConnectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(appConnectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (var tenant in Tenants)
        {
            await using var command = new NpgsqlCommand("""
                select set_config('app.tenant_id', @tenant::text, false);
                insert into identity.members (id, tenant_id, user_id, email, display_name, roles, status, invited_at)
                values (@id, @tenant, null, @email, @name, @roles, 'invited', now())
                on conflict (tenant_id, email) do nothing;
                """, connection);
            command.Parameters.AddWithValue("tenant", tenant.Id);
            command.Parameters.AddWithValue("id", Guid.CreateVersion7());
            command.Parameters.AddWithValue("email", tenant.AdminEmail);
            command.Parameters.AddWithValue("name", $"{tenant.PortalName} admin");
            command.Parameters.AddWithValue("roles", new[] { TenantRoles.TenantAdmin });
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Adds the test consent recipient ("Test finance partner" in both languages) so the consent screen and its check can
    /// be exercised locally (V-13, F-64). As the owner: the application role may only read <c>vendor.recipients</c>.
    /// </summary>
    public static async Task SeedConsentRecipientsAsync(string ownerConnectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            insert into vendor.recipients (id, name_ar, name_en)
            values (@id, 'شريك تمويل تجريبي', 'Test finance partner')
            on conflict (id) do nothing;
            """, connection);
        command.Parameters.AddWithValue("id", TestRecipientId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
