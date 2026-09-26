using Microsoft.EntityFrameworkCore;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Identity.Members;

/// <summary>A row of <c>identity.members</c>; status is "invited" or "active".</summary>
internal sealed class MemberRecord
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string? UserId { get; set; }

    public string Email { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public List<string> Roles { get; set; } = [];

    public string Status { get; set; } = MemberStatuses.Invited;

    public DateTimeOffset InvitedAt { get; set; }

    public DateTimeOffset? ActivatedAt { get; set; }
}

internal static class MemberStatuses
{
    public const string Invited = "invited";
    public const string Active = "active";
}

internal sealed class MembersDbContext(DbContextOptions<MembersDbContext> options, ITenantAccessor tenants) : DbContext(options)
{
    public DbSet<MemberRecord> Members => Set<MemberRecord>();

    // The query filter mirrors the RLS policy for readable failures; PostgreSQL is the enforcing layer.
    private Guid? CurrentTenantId => tenants.Current?.TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("identity");
        modelBuilder.Entity<MemberRecord>(e =>
        {
            e.ToTable("members");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Roles).HasColumnType("text[]");
            e.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });
    }
}
