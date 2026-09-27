using Microsoft.EntityFrameworkCore;
using Npgsql;
using Platform.Modules.Operations.Contracts;
using Platform.Modules.Vendors.Contracts;
using Platform.Modules.Vendors.Documents;
using Platform.Modules.Vendors.Persistence;
using Platform.Shared.Results;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Vendors.Consent;

/// <summary>
/// The vendor consent ledger (F-64, V-12, V-13, ADR-0010) on <c>vendor.consent_events</c>, which the application role may
/// only read and insert, under the company policy: a grant and a revocation are each one inserted row in the company's own
/// vendor context, so a tenant connection can write neither. The acting user must be a vendor admin of the company and
/// is the actor of each row. Each change is audited in the platform audit before its transaction commits, on the audit
/// writer's own connection: when the audit fails the row rolls back; a commit that fails after it leaves an entry for a
/// change that did not happen. Each entry names the host tenant the vendor or the export acted on (null without one).
/// The database holds the actor and no-backdating rules too (migration 0015). Checks go through <c>vendor.consent_grant_in_force</c> (migration 0012), which works
/// without a vendor context, as an export needs.
/// </summary>
internal sealed class ConsentLedger(
    IDbContextFactory<VendorsDbContext> contexts,
    IVendorAccessor vendors,
    ITenantAccessor tenants,
    IActingUserAccessor actingUser,
    IPlatformAudit platformAudit,
    TimeProvider clock) : IConsentLedger
{
    private const string VendorAdminRole = "vendor-admin";
    private const string OneRevocationIndex = "ux_consent_events_revokes";

    public async Task<IReadOnlyList<ConsentRecipient>> ListRecipientsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.Recipients.AsNoTracking()
            .OrderBy(r => r.NameEn)
            .Select(r => new ConsentRecipient(r.Id, r.NameAr, r.NameEn))
            .ToListAsync(cancellationToken);
    }

    public async Task<Result<Guid>> GrantAsync(
        Guid recipientId, ConsentScope scope, DateOnly validFrom, DateOnly validTo, string actorId, CancellationToken cancellationToken = default)
    {
        var companyId = Company(actorId);
        var scopeCode = ScopeCode(scope);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        if (!await IsVendorAdminAsync(db, actorId, cancellationToken))
        {
            return NotVendorAdmin();
        }

        if (validTo < validFrom || validFrom < VendorCompliance.RiyadhToday(clock))
        {
            return Result.Failure<Guid>(Error.Validation(
                ConsentErrors.InvalidPeriod, "A consent period starts today or later and ends on or after its first day."));
        }

        if (!await db.Recipients.AnyAsync(r => r.Id == recipientId, cancellationToken))
        {
            return Result.Failure<Guid>(Error.Validation(ConsentErrors.UnknownRecipient, "This recipient is not on the platform's list."));
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var grantId = Guid.CreateVersion7();
        db.ConsentEvents.Add(new ConsentEventRow
        {
            Id = grantId,
            CompanyId = companyId,
            RecipientId = recipientId,
            Scope = scopeCode,
            Kind = "grant",
            ValidFrom = validFrom,
            ValidTo = validTo,
            ActorId = actorId,
        });
        await db.SaveChangesAsync(cancellationToken);
        await platformAudit.WriteAsync(
            new PlatformAuditEntry(actorId, "vendor.consent_granted", "vendor_company", companyId.ToString(), new Dictionary<string, string?>
            {
                ["tenant_id"] = tenants.Current?.TenantId.ToString(),
                ["grant_id"] = grantId.ToString(),
                ["recipient_id"] = recipientId.ToString(),
                ["scope"] = scopeCode,
                ["valid_from"] = validFrom.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                ["valid_to"] = validTo.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            }),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Result.Success(grantId);
    }

    public async Task<Result<Guid>> RevokeAsync(Guid grantId, string actorId, CancellationToken cancellationToken = default)
    {
        var companyId = Company(actorId);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        if (!await IsVendorAdminAsync(db, actorId, cancellationToken))
        {
            return NotVendorAdmin();
        }

        // Under the company policy: another company's grant is simply not found.
        var grant = await db.ConsentEvents.AsNoTracking()
            .Where(e => e.Id == grantId && e.Kind == "grant")
            .Select(e => new { e.RecipientId, e.Scope })
            .SingleOrDefaultAsync(cancellationToken);
        if (grant is null)
        {
            return Result.Failure<Guid>(Error.NotFound(ConsentErrors.GrantNotFound, "This consent was not found."));
        }

        if (await db.ConsentEvents.AnyAsync(e => e.RevokesGrantId == grantId, cancellationToken))
        {
            return AlreadyRevoked();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var revocationId = Guid.CreateVersion7();
        db.ConsentEvents.Add(new ConsentEventRow
        {
            Id = revocationId,
            CompanyId = companyId,
            RecipientId = grant.RecipientId,
            Scope = grant.Scope,
            Kind = "revoke",
            RevokesGrantId = grantId,
            ActorId = actorId,
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: OneRevocationIndex })
        {
            // Another revocation of the same grant committed since the check above.
            return AlreadyRevoked();
        }

        await platformAudit.WriteAsync(
            new PlatformAuditEntry(actorId, "vendor.consent_revoked", "vendor_company", companyId.ToString(), new Dictionary<string, string?>
            {
                ["tenant_id"] = tenants.Current?.TenantId.ToString(),
                ["grant_id"] = grantId.ToString(),
                ["revocation_id"] = revocationId.ToString(),
                ["recipient_id"] = grant.RecipientId.ToString(),
                ["scope"] = grant.Scope,
            }),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Result.Success(revocationId);
    }

    public async Task<IReadOnlyList<ConsentGrant>> ListAsync(CancellationToken cancellationToken = default)
    {
        _ = vendors.Current ?? throw new InvalidOperationException("The consent ledger is listed in a vendor context; this scope has none.");
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var events = await db.ConsentEvents.AsNoTracking().ToListAsync(cancellationToken);
        var recipientIds = events.Select(e => e.RecipientId).Distinct().ToList();
        var recipients = await db.Recipients.AsNoTracking()
            .Where(r => recipientIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => new ConsentRecipient(r.Id, r.NameAr, r.NameEn), cancellationToken);
        var revocations = events.Where(e => e.Kind == "revoke" && e.RevokesGrantId is not null).ToDictionary(e => e.RevokesGrantId!.Value);
        var today = VendorCompliance.RiyadhToday(clock);

        return
        [
            .. events
                .Where(e => e.Kind == "grant")
                .OrderByDescending(e => e.OccurredAt)
                .ThenByDescending(e => e.Id)
                .Select(e =>
                {
                    var revocation = revocations.GetValueOrDefault(e.Id);
                    var from = e.ValidFrom!.Value;
                    var to = e.ValidTo!.Value;
                    var status = revocation is not null ? ConsentStatus.Revoked
                        : today < from ? ConsentStatus.NotYetValid
                        : today > to ? ConsentStatus.Expired
                        : ConsentStatus.Active;
                    return new ConsentGrant(
                        e.Id, recipients[e.RecipientId], Scope(e.Scope), from, to, e.ActorId, e.OccurredAt,
                        revocation?.ActorId, revocation?.OccurredAt, status);
                }),
        ];
    }

    public async Task<ConsentCheckResult> CheckAsync(
        Guid companyId, Guid recipientId, ConsentScope scope, DateOnly onDate, CancellationToken cancellationToken = default)
    {
        if (vendors.Current is { } vendor && vendor.CompanyId != companyId)
        {
            throw new InvalidOperationException("A vendor checks the consent of its own company only.");
        }

        var scopeCode = ScopeCode(scope);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var grantId = await db.Database.SqlQuery<Guid?>(
            $"select vendor.consent_grant_in_force({companyId}, {recipientId}, {scopeCode}, {onDate}) as \"Value\"").SingleAsync(cancellationToken);
        var result = new ConsentCheckResult(grantId is not null, grantId);
        await platformAudit.WriteAsync(
            new PlatformAuditEntry(actingUser.UserId, "vendor.consent_check", "vendor_company", companyId.ToString(), new Dictionary<string, string?>
            {
                ["tenant_id"] = tenants.Current?.TenantId.ToString(),
                ["result"] = result.Allowed ? "allowed" : "refused",
                ["grant_id"] = grantId?.ToString(),
                ["recipient_id"] = recipientId.ToString(),
                ["scope"] = scopeCode,
                ["on_date"] = onDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            }),
            cancellationToken);
        return result;
    }

    /// <summary>The vendor context's company, for a change made by <paramref name="actorId"/>, who must be the acting user.</summary>
    private Guid Company(string actorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        var vendor = vendors.Current
            ?? throw new InvalidOperationException("Consent is granted or revoked only in the company's vendor context; a tenant never grants for a vendor.");
        if (!string.Equals(actingUser.UserId, actorId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Consent is granted or revoked by the acting user of the request or circuit.");
        }

        return vendor.CompanyId;
    }

    private static Task<bool> IsVendorAdminAsync(VendorsDbContext db, string userId, CancellationToken cancellationToken) =>
        db.VendorUsers.AsNoTracking().AnyAsync(u => u.UserId == userId && u.Role == VendorAdminRole, cancellationToken);

    private static Result<Guid> NotVendorAdmin() =>
        Result.Failure<Guid>(Error.Refused(ConsentErrors.NotVendorAdmin, "Only the company's vendor administrator can change its consent."));

    private static Result<Guid> AlreadyRevoked() =>
        Result.Failure<Guid>(Error.Conflict(ConsentErrors.AlreadyRevoked, "This consent is already revoked."));

    internal static string ScopeCode(ConsentScope scope) => scope switch
    {
        ConsentScope.AwardRecords => "award_records",
        ConsentScope.PoRecords => "po_records",
        ConsentScope.ProfileDocuments => "profile_documents",
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown consent scope."),
    };

    private static ConsentScope Scope(string code) => code switch
    {
        "award_records" => ConsentScope.AwardRecords,
        "po_records" => ConsentScope.PoRecords,
        "profile_documents" => ConsentScope.ProfileDocuments,
        _ => throw new InvalidOperationException($"Unknown consent scope '{code}'."),
    };
}
