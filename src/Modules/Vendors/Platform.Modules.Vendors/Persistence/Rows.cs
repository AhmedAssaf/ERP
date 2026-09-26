namespace Platform.Modules.Vendors.Persistence;

// Row types for the vendor schema (Migrations/0001_vendors.sql). Platform-level rows carry no tenant_id; row-level
// security keys them on the vendor company. Only the relationship row is tenant-scoped.

internal sealed class CompanyRow
{
    public Guid Id { get; set; }

    public string CrNumber { get; set; } = string.Empty;

    public string NameAr { get; set; } = string.Empty;

    public string NameEn { get; set; } = string.Empty;

    public string VatNumber { get; set; } = string.Empty;

    public string? Address { get; set; }

    public string ContactName { get; set; } = string.Empty;

    public string? ContactPhone { get; set; }

    public string ContactEmail { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class VendorUserRow
{
    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public string UserId { get; set; } = string.Empty;

    public string Role { get; set; } = string.Empty;

    public string PrivacyNoticeVersion { get; set; } = string.Empty;

    public DateTimeOffset PrivacyAcceptedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class DocumentRow
{
    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public string Type { get; set; } = string.Empty;

    public DateOnly ExpiresOn { get; set; }

    public string ObjectKey { get; set; } = string.Empty;

    public string Sha256 { get; set; } = string.Empty;

    public string ScanStatus { get; set; } = string.Empty;

    public bool IsCurrent { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class RelationshipRow
{
    public Guid TenantId { get; set; }

    public Guid CompanyId { get; set; }

    public string Status { get; set; } = string.Empty;

    public DateTimeOffset FirstSeenAt { get; set; }

    public string? ApprovedBy { get; set; }
}

internal sealed class RecipientRow
{
    public Guid Id { get; set; }

    public string NameAr { get; set; } = string.Empty;

    public string NameEn { get; set; } = string.Empty;
}

internal sealed class ConsentEventRow
{
    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid RecipientId { get; set; }

    public string Scope { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public DateOnly? ValidFrom { get; set; }

    public DateOnly? ValidTo { get; set; }

    public Guid? RevokesGrantId { get; set; }

    public string ActorId { get; set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; set; }
}
