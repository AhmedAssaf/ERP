namespace Platform.Shared.Data;

/// <summary>
/// The names of the hosts' Npgsql data sources (W-10 final fix wave; spec O-10, O-11). Npgsql puts a data source's name on
/// every connection pool metric (<c>db.client.connection.pool.name</c>) and command span (<c>db.npgsql.data_source</c>); a data
/// source without a name is named after its connection string, host and user included. Every data source a host builds itself
/// carries one of these names. Two pools stay unnamed: the module contexts' (EF Core opens connections from the connection
/// string, sharing the pool of plain connections on it) and Hangfire's LISTEN connection (cloned from a connection string);
/// so the hosts' metric view drops the pool name and the span processor drops the data source tag, whatever the name. The
/// collector keeps a pool name only when it has the <c>platform-</c> prefix (<c>infra/compose/observability/collector.yaml</c>).
/// </summary>
public static class DataSourceNames
{
    /// <summary>The Tenancy module's own pool.</summary>
    public const string Tenancy = "platform-tenancy";

    /// <summary>The Data Protection key ring's pool, as <c>erp_key_ring</c> (W-24).</summary>
    public const string KeyRing = "platform-key-ring";

    /// <summary>Hangfire's storage (<see cref="Jobs.JobsModule"/>).</summary>
    public const string Jobs = "platform-jobs";

    /// <summary>The PostgreSQL health check's unpooled connection (F-51).</summary>
    public const string Health = "platform-health";
}
