using Microsoft.EntityFrameworkCore;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Vendors.Persistence;

internal sealed class VendorsDbContext(
    DbContextOptions<VendorsDbContext> options, ITenantAccessor tenants, IVendorAccessor vendors) : DbContext(options)
{
    public DbSet<CompanyRow> Companies => Set<CompanyRow>();

    public DbSet<VendorUserRow> VendorUsers => Set<VendorUserRow>();

    public DbSet<DocumentRow> Documents => Set<DocumentRow>();

    public DbSet<RelationshipRow> Relationships => Set<RelationshipRow>();

    public DbSet<RecipientRow> Recipients => Set<RecipientRow>();

    public DbSet<ConsentEventRow> ConsentEvents => Set<ConsentEventRow>();

    // Query filters mirror the RLS policies for readable failures; PostgreSQL is the enforcing layer.
    private Guid? CurrentTenantId => tenants.Current?.TenantId;

    private Guid? CurrentCompanyId => vendors.Current?.CompanyId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("vendor");

        modelBuilder.Entity<CompanyRow>(e =>
        {
            e.ToTable("companies");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.CreatedAt).ValueGeneratedOnAdd();
            e.HasQueryFilter(x => x.Id == CurrentCompanyId);
        });

        modelBuilder.Entity<VendorUserRow>(e =>
        {
            e.ToTable("vendor_users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.CreatedAt).ValueGeneratedOnAdd();
            e.HasQueryFilter(x => x.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<DocumentRow>(e =>
        {
            e.ToTable("documents");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.CreatedAt).ValueGeneratedOnAdd();
            e.HasQueryFilter(x => x.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<RelationshipRow>(e =>
        {
            e.ToTable("relationships");
            e.HasKey(x => new { x.TenantId, x.CompanyId });
            e.Property(x => x.FirstSeenAt).ValueGeneratedOnAdd();
            e.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<RecipientRow>(e =>
        {
            e.ToTable("recipients");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<ConsentEventRow>(e =>
        {
            e.ToTable("consent_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.OccurredAt).ValueGeneratedOnAdd();
            e.HasQueryFilter(x => x.CompanyId == CurrentCompanyId);
        });
    }
}
