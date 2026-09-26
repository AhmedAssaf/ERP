using Microsoft.EntityFrameworkCore;

namespace Platform.Modules.Operations;

/// <summary>
/// Platform-level tables (ops schema): no tenant_id, no query filter. The tenant connection interceptor still runs
/// (AddModuleDbContext) and sets an empty tenant when none is set, which is harmless here since there is no RLS
/// policy on these tables (spec section 6): access is guarded by grants only.
/// </summary>
internal sealed class OperationsDbContext(DbContextOptions<OperationsDbContext> options) : DbContext(options)
{
    public DbSet<HealthResultRow> HealthResults => Set<HealthResultRow>();

    public DbSet<IncidentRow> Incidents => Set<IncidentRow>();

    public DbSet<PlatformAuditRow> PlatformAudit => Set<PlatformAuditRow>();

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
    }
}
