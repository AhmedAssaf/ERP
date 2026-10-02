namespace Platform.Shared.Data;

/// <summary>
/// The names of the hosts' Npgsql data sources (W-10 final fix wave; spec O-10, O-11). Npgsql puts a data source's name on
/// every connection pool metric (<c>db.client.connection.pool.name</c>) and command span (<c>db.npgsql.data_source</c>); a data
/// source without a name is named after its connection string, host and user included. So every data source a host opens is
/// built with one of these names, never from a bare connection string. The collector keeps a pool name only when it has the
/// <c>platform-</c> prefix (<c>infra/compose/observability/collector.yaml</c>).
/// </summary>
public static class DataSourceNames
{
    /// <summary>The module contexts' shared pool (<see cref="ModuleDbContextRegistration.AddModuleDbContext{TContext}"/>).</summary>
    public const string App = "platform-app";

    /// <summary>The Tenancy module's own pool.</summary>
    public const string Tenancy = "platform-tenancy";

    /// <summary>The Data Protection key ring's pool, as <c>erp_key_ring</c> (W-24).</summary>
    public const string KeyRing = "platform-key-ring";

    /// <summary>Hangfire's storage (<see cref="Jobs.JobsModule"/>).</summary>
    public const string Jobs = "platform-jobs";

    /// <summary>The PostgreSQL health check's unpooled connection (F-51).</summary>
    public const string Health = "platform-health";
}
