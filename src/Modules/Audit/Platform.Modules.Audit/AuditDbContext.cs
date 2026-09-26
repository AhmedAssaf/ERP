using Microsoft.EntityFrameworkCore;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Audit;

internal sealed class AuditDbContext(DbContextOptions<AuditDbContext> options, ITenantAccessor tenants) : DbContext(options)
{
    public DbSet<AuditEvent> Events => Set<AuditEvent>();

    // The query filter mirrors the RLS policy for readable failures; PostgreSQL is the enforcing layer.
    private Guid? CurrentTenantId => tenants.Current?.TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("audit");
        modelBuilder.Entity<AuditEvent>(e =>
        {
            e.ToTable("events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Data).HasColumnType("jsonb");
            e.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });
    }
}
