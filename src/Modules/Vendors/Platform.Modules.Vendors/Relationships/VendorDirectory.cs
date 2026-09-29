using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Platform.Modules.Audit.Contracts;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Operations.Contracts;
using Platform.Modules.Vendors.Contracts;
using Platform.Modules.Vendors.Documents;
using Platform.Modules.Vendors.Ownership;
using Platform.Modules.Vendors.Persistence;
using Platform.Modules.Vendors.Registration;
using Platform.Shared.Results;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Vendors.Relationships;

/// <summary>
/// The current tenant's vendors for its staff (V-7, V-11). Every read goes through the security-definer functions that
/// answer only for companies related to <c>platform.current_tenant()</c> (<c>vendor.related_companies</c>,
/// <c>vendor.related_current_documents</c>, <c>vendor.related_company</c>, <c>vendor.related_documents</c>); the
/// relationship row itself is read under the tenant policy. Approval re-checks the actor's role in <c>identity.members</c>
/// at the moment it runs (the circuit's claims date from when the page opened), and the database records the acting user
/// of the session as the approver, never an argument. A scope with a vendor context is refused, as the functions refuse it.
/// W-33: a company's first approval by any tenant needs its ownership verified; the officer confirms it in the same
/// transaction (<c>vendor.verify_ownership</c>), after seeing the registering person (from the identity provider) and the
/// lookup of the platform's method (<see cref="ICrOwnershipVerifier"/>). Another tenant learns only that it was verified.
/// </summary>
internal sealed partial class VendorDirectory(
    IDbContextFactory<VendorsDbContext> contexts,
    ITenantAccessor tenants,
    IVendorAccessor vendors,
    IActingUserAccessor actingUser,
    IMemberDirectory members,
    IAuditWriter audit,
    IPlatformAudit platformAudit,
    IVendorAccounts accounts,
    IEnumerable<ICrOwnershipVerifier> verifiers,
    TimeProvider clock,
    ILogger<VendorDirectory> logger) : IVendorDirectory
{
    public async Task<IReadOnlyList<RelatedVendor>> ListRelatedAsync(CancellationToken cancellationToken = default)
    {
        RequireTenant();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var companies = await db.Database.SqlQuery<CompanyListRow>($"""
            select id, cr_number, name_ar, name_en, status, first_seen_at
            from vendor.related_companies()
            """).ToListAsync(cancellationToken);
        var documents = (await db.Database.SqlQuery<CurrentDocumentRow>($"""
            select company_id, type, expires_on
            from vendor.related_current_documents()
            """).ToListAsync(cancellationToken))
            .ToLookup(d => d.CompanyId);
        var today = VendorCompliance.RiyadhToday(clock);

        return
        [
            .. companies
                .OrderBy(c => c.NameEn, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.CrNumber, StringComparer.Ordinal)
                .Select(c => new RelatedVendor(
                    c.Id, c.CrNumber, c.NameAr, c.NameEn, Status(c.Status), c.FirstSeenAt,
                    VendorCompliance.Blocking(documents[c.Id].Select(d => (d.Type, d.ExpiresOn)), today))),
        ];
    }

    public async Task<RelatedVendorDetails?> GetRelatedAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        RequireTenant();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var relationship = await db.Relationships.AsNoTracking()
            .Where(r => r.CompanyId == companyId)
            .Select(r => new { r.Status, r.FirstSeenAt, r.ApprovedBy })
            .SingleOrDefaultAsync(cancellationToken);
        if (relationship is null)
        {
            return null;
        }

        var company = await db.Database.SqlQuery<CompanyCardRow>($"""
            select id, cr_number, name_ar, name_en, vat_number, address, contact_name, contact_phone, contact_email
            from vendor.related_company({companyId})
            """).SingleOrDefaultAsync(cancellationToken);
        if (company is null)
        {
            return null;
        }

        var documents = await db.Database.SqlQuery<DocumentListRow>($"""
            select id, type, expires_on, is_current, created_at
            from vendor.related_documents({companyId})
            order by created_at desc
            """).ToListAsync(cancellationToken);
        var blocking = VendorCompliance.Blocking(
            documents.Where(d => d.IsCurrent).Select(d => (d.Type, d.ExpiresOn)), VendorCompliance.RiyadhToday(clock));

        return new RelatedVendorDetails(
            company.Id, company.CrNumber, company.NameAr, company.NameEn, company.VatNumber, company.Address,
            company.ContactName, company.ContactPhone, company.ContactEmail,
            Status(relationship.Status), relationship.FirstSeenAt, relationship.ApprovedBy,
            [.. documents.Select(d => new RelatedVendorDocument(d.Id, d.Type, d.ExpiresOn, d.IsCurrent, d.CreatedAt))],
            blocking);
    }

    public Task<Result<VendorRelationshipStatus>> ApproveAsync(Guid companyId, string actorId, CancellationToken cancellationToken = default) =>
        ApproveCoreAsync(companyId, actorId, null, cancellationToken);

    public Task<Result<VendorRelationshipStatus>> ApproveAsync(
        Guid companyId, string actorId, OwnershipConfirmation confirmation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        return ApproveCoreAsync(companyId, actorId, confirmation, cancellationToken);
    }

    public async Task<OwnershipCheck?> GetOwnershipCheckAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        RequireTenant();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var ownership = await OwnershipAsync(db, companyId, cancellationToken);
        var company = ownership is null ? null : await CompanyAsync(db, companyId, cancellationToken);
        if (ownership is null || company is null)
        {
            return null;
        }

        if (ownership.Verified)
        {
            // Verified here or at another tenant: which one, by whom and with what note is never told (ADR-0008).
            return new OwnershipCheck(
                companyId, company.CrNumber, Verified: true, OwnershipStore.VerificationMethod(ownership.Method), ownership.Disputed,
                ownership.HasCertificate, Registrant: null, Lookup: null);
        }

        var method = OwnershipStore.Method((await OwnershipStore.SettingsAsync(db, cancellationToken)).Method);
        var verifier = verifiers.Single(v => v.Method == method);
        var registrant = ownership.RegistrantUserId is { } registrantId ? await RegistrantAsync(registrantId, cancellationToken) : null;
        var lookup = await verifier.LookupAsync(company.CrNumber, cancellationToken);
        return new OwnershipCheck(
            companyId, company.CrNumber, Verified: false, VerifiedMethod: null, ownership.Disputed, ownership.HasCertificate, registrant, lookup);
    }

    private async Task<Result<VendorRelationshipStatus>> ApproveCoreAsync(
        Guid companyId, string actorId, OwnershipConfirmation? confirmation, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        RequireTenant();
        if (!string.Equals(actingUser.UserId, actorId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A vendor is approved by the acting user of the request or circuit.");
        }

        var roles = await members.GetRolesAsync(actorId, cancellationToken);
        if (!roles.Contains(TenantRoles.ContractsOfficer, StringComparer.Ordinal) && !roles.Contains(TenantRoles.TenantAdmin, StringComparer.Ordinal))
        {
            return NotAllowed();
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var company = await CompanyAsync(db, companyId, cancellationToken);
        var ownership = company is null ? null : await OwnershipAsync(db, companyId, cancellationToken);
        if (company is null || ownership is null)
        {
            return NotFound();
        }

        if (!ownership.Verified && confirmation is not null
            && await VerifyAsync(db, company, confirmation, actorId, cancellationToken) is { } refused)
        {
            return refused;
        }

        // Holds the relationship row until the commit, so a second approval waits and then finds it approved. The database
        // checks the approver again (migration 0016: an active officer or admin of the tenant who is no vendor user), and
        // since migration 0018 that the company's ownership is verified and not disputed.
        bool approved;
        try
        {
            approved = await db.Database.SqlQuery<bool>($"select vendor.approve_relationship({companyId}) as \"Value\"").SingleAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            return NotAllowed();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.CheckViolation && ex.ConstraintName == "ck_relationships_ownership_verified")
        {
            return Refused(CrOwnershipErrors.Unverified, "Confirm the company's ownership against its CR certificate before its first approval.");
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.CheckViolation && ex.ConstraintName == "ck_relationships_not_disputed")
        {
            return Disputed();
        }

        if (!approved)
        {
            return Result.Failure<VendorRelationshipStatus>(Error.Conflict(
                VendorDirectoryErrors.AlreadyApproved, "This vendor is already approved."));
        }

        // Before the commit, so an approval is never recorded without its audit entry: when the audit fails the approval
        // rolls back. A commit that fails after it leaves an entry for an approval that did not happen; approving again
        // then succeeds and writes a second entry.
        await audit.WriteAsync(
            new AuditEntry(actorId, "vendor.approved", "vendor_company", companyId.ToString(), new Dictionary<string, string?>
            {
                ["cr_number"] = company.CrNumber,
                ["name_en"] = company.NameEn,
            }),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Result.Success(VendorRelationshipStatus.Approved);
    }

    /// <summary>
    /// Records the officer's confirmation of ownership inside the approval's transaction and audits it in the tenant's log
    /// and the platform audit (both before the commit, as the approval's own entry). Null when it was recorded, or another
    /// officer recorded one meanwhile (the first record stays); the refusal otherwise.
    /// </summary>
    private async Task<Result<VendorRelationshipStatus>?> VerifyAsync(
        VendorsDbContext db, CompanyCardRow company, OwnershipConfirmation confirmation, string actorId, CancellationToken cancellationToken)
    {
        if (!VendorInput.IsFreeText(confirmation.Note, OwnershipStore.MaxNoteLength))
        {
            return NoteRequired();
        }

        var note = confirmation.Note!.Trim();
        var method = confirmation.BasedOnWathq ? "wathq" : "manual";
        bool recorded;
        try
        {
            recorded = await db.Database.SqlQuery<bool>(
                $"select vendor.verify_ownership({company.Id}, {method}, {note}) as \"Value\"").SingleAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            return NotAllowed();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.CheckViolation)
        {
            return ex.ConstraintName switch
            {
                "ck_ownership_certificate" => Refused(
                    CrOwnershipErrors.NoCertificate, "The company has no current CR certificate to check. Ask the vendor to upload it."),
                "ck_ownership_not_disputed" => Disputed(),
                "ck_ownership_method_setting" => Refused(
                    CrOwnershipErrors.WathqNotSelected, "The platform checks ownership by hand; confirm it against the CR certificate."),
                "ck_ownership_verifications_note" => NoteRequired(),
                _ => throw new InvalidOperationException($"Ownership verification refused under an unexpected rule ({ex.ConstraintName}).", ex),
            };
        }

        if (!recorded)
        {
            return null;
        }

        var outcome = OwnershipStore.Code(confirmation.LookupOutcome);
        await audit.WriteAsync(
            new AuditEntry(actorId, "vendor.ownership_verified", "vendor_company", company.Id.ToString(), new Dictionary<string, string?>
            {
                ["cr_number"] = company.CrNumber,
                ["method"] = method,
                ["wathq_outcome"] = outcome,
                ["note"] = note,
            }),
            cancellationToken);
        await platformAudit.WriteAsync(
            new PlatformAuditEntry(actorId, "vendor.ownership_verified", "vendor_company", company.Id.ToString(), new Dictionary<string, string?>
            {
                ["tenant"] = tenants.Current!.Slug,
                ["method"] = method,
                ["wathq_outcome"] = outcome,
            }),
            cancellationToken);
        return null;
    }

    /// <summary>The registering person's name and email from the identity provider; unknown parts when it does not answer.</summary>
    private async Task<VendorRegistrant> RegistrantAsync(string userId, CancellationToken cancellationToken)
    {
        try
        {
            var profile = await accounts.ProfileAsync(userId, cancellationToken);
            var name = string.Join(' ', new[] { profile?.FirstName, profile?.LastName }.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));
            return new VendorRegistrant(userId, name.Length == 0 ? null : name, string.IsNullOrWhiteSpace(profile?.Email) ? null : profile.Email);
        }
        catch (Exception ex) when (ex is IdentityProviderException or InvalidOperationException)
        {
            RegistrantUnknown(logger, userId, ex.GetType().Name);
            return new VendorRegistrant(userId, null, null);
        }
    }

    private static async Task<CompanyCardRow?> CompanyAsync(VendorsDbContext db, Guid companyId, CancellationToken cancellationToken) =>
        await db.Database.SqlQuery<CompanyCardRow>($"""
            select id, cr_number, name_ar, name_en, vat_number, address, contact_name, contact_phone, contact_email
            from vendor.related_company({companyId})
            """).SingleOrDefaultAsync(cancellationToken);

    private static async Task<OwnershipRow?> OwnershipAsync(VendorsDbContext db, Guid companyId, CancellationToken cancellationToken) =>
        await db.Database.SqlQuery<OwnershipRow>($"""
            select registrant_user_id, verified, method, disputed, has_certificate
            from vendor.related_ownership({companyId})
            """).SingleOrDefaultAsync(cancellationToken);

    private static Result<VendorRelationshipStatus> Refused(string code, string message) =>
        Result.Failure<VendorRelationshipStatus>(Error.Refused(code, message));

    private static Result<VendorRelationshipStatus> NoteRequired() =>
        Result.Failure<VendorRelationshipStatus>(Error.Validation(
            CrOwnershipErrors.NoteRequired, $"Write a note of up to {OwnershipStore.MaxNoteLength} characters on what you checked."));

    private static Result<VendorRelationshipStatus> Disputed() =>
        Refused(CrOwnershipErrors.Disputed, "WaslaBid is reviewing who owns this company. It can be approved once the review is closed.");

    [LoggerMessage(Level = LogLevel.Warning, Message = "The identity provider did not describe the registering user {UserId} for the ownership check ({ErrorType}).")]
    private static partial void RegistrantUnknown(ILogger logger, string userId, string errorType);

    // Tenant staff only: a vendor on a tenant host must never read other companies related to that tenant. The database
    // functions refuse a vendor context too (migration 0011); this says so before any query.
    private void RequireTenant()
    {
        if (tenants.Current is null)
        {
            throw new InvalidOperationException("The vendor directory is read by tenant staff on a tenant host; this scope has none.");
        }

        if (vendors.Current is not null)
        {
            throw new InvalidOperationException("The vendor directory is tenant staff's; a scope with a vendor context may not use it.");
        }
    }

    private static Result<VendorRelationshipStatus> NotAllowed() =>
        Result.Failure<VendorRelationshipStatus>(Error.Refused(
            VendorDirectoryErrors.NotAllowed, "Only a contracts officer or a tenant administrator can approve a vendor."));

    private static Result<VendorRelationshipStatus> NotFound() =>
        Result.Failure<VendorRelationshipStatus>(Error.NotFound(VendorDirectoryErrors.NotFound, "This vendor does not work with this organization."));

    private static VendorRelationshipStatus Status(string status) => status switch
    {
        "pending" => VendorRelationshipStatus.Pending,
        "approved" => VendorRelationshipStatus.Approved,
        _ => throw new InvalidOperationException($"Unknown relationship status '{status}'."),
    };

    // Rows of the functions' result sets; columns map by the snake_case convention.
    private sealed class CompanyListRow
    {
        public Guid Id { get; set; }

        public string CrNumber { get; set; } = string.Empty;

        public string NameAr { get; set; } = string.Empty;

        public string NameEn { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public DateTimeOffset FirstSeenAt { get; set; }
    }

    private sealed class CurrentDocumentRow
    {
        public Guid CompanyId { get; set; }

        public string Type { get; set; } = string.Empty;

        public DateOnly ExpiresOn { get; set; }
    }

    private sealed class CompanyCardRow
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
    }

    private sealed class OwnershipRow
    {
        public string? RegistrantUserId { get; set; }

        public bool Verified { get; set; }

        public string? Method { get; set; }

        public bool Disputed { get; set; }

        public bool HasCertificate { get; set; }
    }

    private sealed class DocumentListRow
    {
        public Guid Id { get; set; }

        public string Type { get; set; } = string.Empty;

        public DateOnly ExpiresOn { get; set; }

        public bool IsCurrent { get; set; }

        public DateTimeOffset CreatedAt { get; set; }
    }
}
