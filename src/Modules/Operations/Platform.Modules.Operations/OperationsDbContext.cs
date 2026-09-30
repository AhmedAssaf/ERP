using Microsoft.EntityFrameworkCore;

namespace Platform.Modules.Operations;

/// <summary>
/// Platform-level tables (ops schema): no tenant_id, no query filter. The tenant connection interceptor still runs
/// (AddModuleDbContext) and sets an empty tenant when none is set. Health results and incidents are guarded by grants
/// only (spec section 6); the platform audit (operations 0004) and the active-user counts (0006, W-10) are also under
/// forced row-level security that admits only a session with neither a tenant nor a vendor context.
/// </summary>
internal sealed class OperationsDbContext(DbContextOptions<OperationsDbContext> options) : DbContext(options)
{
    public DbSet<HealthResultRow> HealthResults => Set<HealthResultRow>();

    public DbSet<IncidentRow> Incidents => Set<IncidentRow>();

    public DbSet<PlatformAuditRow> PlatformAudit => Set<PlatformAuditRow>();

    public DbSet<Usage.ActiveUserCountRow> ActiveUserCounts => Set<Usage.ActiveUserCountRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("ops");

        modelBuilder.Entity<HealthResultRow>(e =>
        {
            e.ToTable("health_results");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Status).HasConversion<string>();
        });

        modelBuilder.Entity<IncidentRow>(e =>
        {
            e.ToTable("incidents");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<PlatformAuditRow>(e =>
        {
            e.ToTable("platform_audit");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Data).HasColumnType("jsonb");
        });

        // W-10 (operations 0006): no key; the usage job replaces every row with raw SQL, the console only reads.
        modelBuilder.Entity<Usage.ActiveUserCountRow>(e =>
        {
            e.ToTable("active_user_counts");
            e.HasNoKey();
        });
    }
}
