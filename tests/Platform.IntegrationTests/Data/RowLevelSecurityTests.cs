using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Audit;
using Platform.Modules.Audit.Contracts;

namespace Platform.IntegrationTests.Data;

/// <summary>W-03 acceptance, run against audit.events as the first tenant-owned table.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class RowLevelSecurityTests(DatabaseFixture db) : IAsyncLifetime
{
    private const string SeedAction = "rls.seed";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private ModuleHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = new ModuleHost(db.AppConnectionString);
        foreach (var tenant in new[] { TestTenants.Acme, TestTenants.Beta })
        {
            await using var scope = _host.ScopeFor(tenant);
            await scope.ServiceProvider.GetRequiredService<IAuditWriter>()
                .WriteAsync(new AuditEntry("rls-test", SeedAction, "tenant", tenant.Slug), Ct);
        }
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    [Fact]
    public async Task Ef_query_returns_only_the_current_tenants_rows()
    {
        await using var scope = _host.ScopeFor(TestTenants.Acme);
        await using var context = await CreateContextAsync(scope);

        var tenants = await context.Events.IgnoreQueryFilters()
            .Where(e => e.Action == SeedAction).Select(e => e.TenantId).Distinct().ToListAsync(Ct);

        tenants.ShouldBe([TestTenants.Acme.TenantId]);
    }

    [Fact]
    public async Task Raw_sql_as_the_app_role_returns_only_the_current_tenants_rows()
    {
        await using var scope = _host.ScopeFor(TestTenants.Acme);
        await using var context = await CreateContextAsync(scope);

        var tenants = await context.Database
            .SqlQueryRaw<Guid>("select tenant_id as \"Value\" from audit.events where action = 'rls.seed'")
            .Distinct().ToListAsync(Ct);

        tenants.ShouldBe([TestTenants.Acme.TenantId]);
    }

    [Fact]
    public async Task No_tenant_set_returns_zero_rows_and_an_insert_is_rejected()
    {
        await using var scope = _host.ScopeFor(null);
        await using var context = await CreateContextAsync(scope);

        var count = await context.Events.IgnoreQueryFilters().CountAsync(Ct);
        count.ShouldBe(0);

        await Should.ThrowAsync<PostgresException>(() => context.Database.ExecuteSqlRawAsync(
            "insert into audit.events (id, tenant_id, action, subject_type) values (gen_random_uuid(), '0f0e0d0c-0000-7000-8000-00000000ac01', 'x', 'y')",
            Ct));
    }

    [Fact]
    public async Task A_pooled_connection_reused_by_another_tenant_sees_none_of_the_first_tenants_rows()
    {
        // One connection in the pool forces both scopes onto the same PostgreSQL backend.
        var singleConnection = new NpgsqlConnectionStringBuilder(db.AppConnectionString) { MaxPoolSize = 1 }.ConnectionString;
        await using var host = new ModuleHost(singleConnection);

        int acmeBackend;
        await using (var acme = host.ScopeFor(TestTenants.Acme))
        {
            await using var context = await CreateContextAsync(acme);
            acmeBackend = await context.Database.SqlQueryRaw<int>("select pg_backend_pid() as \"Value\"").SingleAsync(Ct);
            (await context.Events.CountAsync(Ct)).ShouldBeGreaterThan(0);
        }

        await using var beta = host.ScopeFor(TestTenants.Beta);
        await using var betaContext = await CreateContextAsync(beta);
        var betaBackend = await betaContext.Database.SqlQueryRaw<int>("select pg_backend_pid() as \"Value\"").SingleAsync(Ct);
        var tenants = await betaContext.Events.IgnoreQueryFilters().Select(e => e.TenantId).Distinct().ToListAsync(Ct);

        betaBackend.ShouldBe(acmeBackend);
        tenants.ShouldBe([TestTenants.Beta.TenantId]);
    }

    private static Task<AuditDbContext> CreateContextAsync(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IDbContextFactory<AuditDbContext>>().CreateDbContextAsync(Ct);
}
