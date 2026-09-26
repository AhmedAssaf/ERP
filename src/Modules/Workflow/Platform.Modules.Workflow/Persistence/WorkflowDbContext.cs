using Microsoft.EntityFrameworkCore;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Workflow.Persistence;

internal sealed class WorkflowDbContext(DbContextOptions<WorkflowDbContext> options, ITenantAccessor tenants) : DbContext(options)
{
    public DbSet<DefinitionRow> Definitions => Set<DefinitionRow>();

    public DbSet<DefinitionStepRow> DefinitionSteps => Set<DefinitionStepRow>();

    public DbSet<TenderWorkflowRow> TenderWorkflows => Set<TenderWorkflowRow>();

    public DbSet<TenderStepRow> TenderSteps => Set<TenderStepRow>();

    public DbSet<StepDecisionRow> Decisions => Set<StepDecisionRow>();

    // Query filters mirror the RLS policies for readable failures; PostgreSQL is the enforcing layer.
    private Guid? CurrentTenantId => tenants.Current?.TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("workflow");

        modelBuilder.Entity<DefinitionRow>(e =>
        {
            e.ToTable("workflow_definition");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasMany(x => x.Steps).WithOne().HasForeignKey(s => s.DefinitionId);
            e.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<DefinitionStepRow>(e =>
        {
            e.ToTable("workflow_step");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Stage).HasConversion<string>();
            e.Property(x => x.Rule).HasConversion<string>();
            e.Property(x => x.Threshold).HasPrecision(18, 2);
            e.HasIndex(x => new { x.DefinitionId, x.Position }).IsUnique();
            e.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<TenderWorkflowRow>(e =>
        {
            e.ToTable("tender_workflow");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Snapshot).HasColumnType("jsonb");
            e.Property(x => x.State).HasConversion<string>();
            e.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
            e.HasMany(x => x.Steps).WithOne().HasForeignKey(s => s.TenderWorkflowId);
            e.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<TenderStepRow>(e =>
        {
            e.ToTable("tender_workflow_step");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Stage).HasConversion<string>();
            e.Property(x => x.Rule).HasConversion<string>();
            e.Property(x => x.Status).HasConversion<string>();
            e.HasIndex(x => new { x.TenderWorkflowId, x.Position }).IsUnique();
            e.HasMany(x => x.Decisions).WithOne().HasForeignKey(d => d.StepId);
            e.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<StepDecisionRow>(e =>
        {
            e.ToTable("step_decision");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Decision).HasConversion<string>();
            e.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });
    }
}
