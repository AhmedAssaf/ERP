using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Operations.Contracts;
using Platform.Modules.Vendors.Contracts;
using Platform.Modules.Vendors.Persistence;
using Platform.Modules.Vendors.Registration;
using Platform.Shared.Results;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Vendors.Ownership;

/// <summary>
/// The platform console's side of W-33 (<see cref="ICrOwnershipAdministration"/>): the check method and the review of
/// disputes. Every call needs a platform request (the console's scope) without a tenant or vendor context, and changes
/// run as the acting user; the database functions refuse any other session themselves (migration 0018). Each change and
/// its platform audit entry commit together; the audit is written before the commit, so no change is recorded without it.
/// After an upheld dispute commits, the identity provider is updated: the claimant gets the realm role <c>vendor</c> and
/// the removed users lose it. The database change stands if that fails; the failure is logged and reported to the admin,
/// who can repeat it in Keycloak.
/// </summary>
internal sealed partial class CrOwnershipAdministration(
    IDbContextFactory<VendorsDbContext> contexts,
    IPlatformRequestContext platform,
    ITenantAccessor tenants,
    IVendorAccessor vendors,
    IActingUserAccessor actingUser,
    IPlatformAudit audit,
    IVendorAccounts accounts,
    IOptions<WathqOptions> wathq,
    ILogger<CrOwnershipAdministration> logger) : ICrOwnershipAdministration
{
    public async Task<CrOwnershipSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        RequirePlatform();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var settings = await OwnershipStore.SettingsAsync(db, cancellationToken);
        return new CrOwnershipSettings(OwnershipStore.Method(settings.Method), wathq.Value.IsConfigured, settings.ChangedBy, settings.ChangedAt);
    }

    public async Task<Result<CrOwnershipMethod>> SetMethodAsync(CrOwnershipMethod method, string actorId, CancellationToken cancellationToken = default)
    {
        RequireActor(actorId);
        var code = OwnershipStore.Code(method);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var before = await db.Database.SqlQuery<string>($"select vendor.set_ownership_method({code}) as \"Value\"").SingleAsync(cancellationToken);
        await audit.WriteAsync(
            new PlatformAuditEntry(actorId, "vendor.ownership_method_changed", "platform_setting", "cr_ownership_method", new Dictionary<string, string?>
            {
                ["from"] = before,
                ["to"] = code,
                ["wathq_configured"] = wathq.Value.IsConfigured ? "true" : "false",
            }),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Result.Success(OwnershipStore.Method(before));
    }

    public async Task<IReadOnlyList<CrDispute>> ListOpenDisputesAsync(CancellationToken cancellationToken = default)
    {
        RequirePlatform();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var rows = await db.Database.SqlQuery<OpenDisputeRow>($"""
            select id, company_id, cr_number, name_ar, name_en, claimant_user_id, claimant_email, claimant_name, statement,
                   raised_on_tenant, raised_at, registrant_user_id, ownership_method
            from vendor.open_cr_disputes()
            """).ToListAsync(cancellationToken);
        return
        [
            .. rows.Select(r => new CrDispute(
                r.Id, r.CompanyId, r.CrNumber, r.NameAr, r.NameEn, r.ClaimantUserId, r.ClaimantEmail, r.ClaimantName, r.Statement,
                r.RaisedOnTenant, r.RaisedAt, r.RegistrantUserId, OwnershipStore.VerificationMethod(r.OwnershipMethod))),
        ];
    }

    public async Task<Result<CrDisputeUpheld>> UpholdAsync(Guid disputeId, string? note, string actorId, CancellationToken cancellationToken = default)
    {
        RequireActor(actorId);
        if (!VendorInput.IsFreeText(note, OwnershipStore.MaxNoteLength))
        {
            return Result.Failure<CrDisputeUpheld>(NoteRequired());
        }

        var resolved = await ResolveAsync(disputeId, uphold: true, note!.Trim(), actorId, "vendor.dispute_upheld", cancellationToken);
        if (!resolved.IsSuccess)
        {
            return Result.Failure<CrDisputeUpheld>(resolved.Error);
        }

        var row = resolved.Value;
        var removed = row.RemovedUserIds ?? [];
        var updated = await UpdateIdentityProviderAsync(disputeId, row.ClaimantUserId, removed);
        return Result.Success(new CrDisputeUpheld(row.CompanyId, row.ClaimantUserId, removed, updated));
    }

    public async Task<Result<Guid>> RejectAsync(Guid disputeId, string? note, string actorId, CancellationToken cancellationToken = default)
    {
        RequireActor(actorId);
        if (!VendorInput.IsFreeText(note, OwnershipStore.MaxNoteLength))
        {
            return Result.Failure<Guid>(NoteRequired());
        }

        var resolved = await ResolveAsync(disputeId, uphold: false, note!.Trim(), actorId, "vendor.dispute_rejected", cancellationToken);
        return resolved.IsSuccess ? Result.Success(disputeId) : Result.Failure<Guid>(resolved.Error);
    }

    private async Task<Result<ResolvedRow>> ResolveAsync(
        Guid disputeId, bool uphold, string note, string actorId, string action, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        ResolvedRow? row;
        try
        {
            row = (await db.Database.SqlQuery<ResolvedRow>($"""
                select company_id, claimant_user_id, removed_user_ids from vendor.resolve_cr_dispute({disputeId}, {uphold}, {note})
                """).ToListAsync(cancellationToken)).SingleOrDefault();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.CheckViolation && ex.ConstraintName == "ck_cr_disputes_claimant_not_vendor")
        {
            return Result.Failure<ResolvedRow>(Error.Conflict(
                CrDisputeErrors.AlreadyVendor, "The claimant's account meanwhile belongs to a vendor company; it cannot take over this one."));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.CheckViolation && ex.ConstraintName == "ck_cr_disputes_claimant_not_staff")
        {
            return Result.Failure<ResolvedRow>(Error.Conflict(
                CrDisputeErrors.StaffAccount, "The claimant's account meanwhile belongs to a staff member of a tenant; it cannot become a vendor admin."));
        }

        if (row is null)
        {
            return Result.Failure<ResolvedRow>(Error.Conflict(CrDisputeErrors.NotOpen, "This dispute is no longer open."));
        }

        await audit.WriteAsync(
            new PlatformAuditEntry(actorId, action, "cr_dispute", disputeId.ToString(), new Dictionary<string, string?>
            {
                ["company_id"] = row.CompanyId.ToString(),
                ["claimant"] = row.ClaimantUserId,
                ["removed_users"] = row.RemovedUserIds is null ? null : string.Join(',', row.RemovedUserIds),
                ["note"] = note,
            }),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Result.Success(row);
    }

    /// <summary>
    /// The claimant gets the realm role <c>vendor</c> (so the Vendor policy opens once they sign in again and rejoin each
    /// tenant's organization through <c>/vendor/join</c>); every removed user loses it. True when the grant succeeded; the
    /// removals are best effort in <see cref="IVendorAccounts.RevokeAsync"/>, which logs its own failures.
    /// </summary>
    private async Task<bool> UpdateIdentityProviderAsync(Guid disputeId, string claimant, IReadOnlyList<string> removed)
    {
        var updated = true;
        try
        {
            await accounts.GrantRoleAsync(claimant, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IdentityProviderException or InvalidOperationException)
        {
            GrantFailed(logger, disputeId, claimant, ex.GetType().Name);
            updated = false;
        }

        foreach (var user in removed.Where(u => !string.Equals(u, claimant, StringComparison.Ordinal)))
        {
            try
            {
                await accounts.RevokeAsync(new VendorAccessGrant(user, string.Empty, RoleAdded: true, OrganizationAdded: false), CancellationToken.None);
            }
            catch (Exception ex) when (ex is IdentityProviderException or InvalidOperationException)
            {
                RevokeFailed(logger, disputeId, user, ex.GetType().Name);
                updated = false;
            }
        }

        return updated;
    }

    private void RequirePlatform()
    {
        if (!platform.IsPlatform || tenants.Current is not null || vendors.Current is not null)
        {
            throw new InvalidOperationException("CR ownership is administered in the platform console only.");
        }
    }

    private void RequireActor(string actorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        RequirePlatform();
        if (!string.Equals(actingUser.UserId, actorId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("CR ownership is administered by the acting user of the console request or circuit.");
        }
    }

    private static Error NoteRequired() =>
        Error.Validation(CrDisputeErrors.NoteRequired, $"Write a note of up to {OwnershipStore.MaxNoteLength} characters on what you checked.");

    [LoggerMessage(Level = LogLevel.Error, Message = "Dispute {DisputeId} was upheld, but Keycloak did not grant the vendor role to the claimant {UserId} ({ErrorType}); grant it in Keycloak.")]
    private static partial void GrantFailed(ILogger logger, Guid disputeId, string userId, string errorType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Dispute {DisputeId} was upheld, but the vendor role of removed user {UserId} was not taken back ({ErrorType}); remove it in Keycloak.")]
    private static partial void RevokeFailed(ILogger logger, Guid disputeId, string userId, string errorType);

    private sealed class OpenDisputeRow
    {
        public Guid Id { get; set; }

        public Guid CompanyId { get; set; }

        public string CrNumber { get; set; } = string.Empty;

        public string NameAr { get; set; } = string.Empty;

        public string NameEn { get; set; } = string.Empty;

        public string ClaimantUserId { get; set; } = string.Empty;

        public string ClaimantEmail { get; set; } = string.Empty;

        public string ClaimantName { get; set; } = string.Empty;

        public string Statement { get; set; } = string.Empty;

        public Guid RaisedOnTenant { get; set; }

        public DateTimeOffset RaisedAt { get; set; }

        public string? RegistrantUserId { get; set; }

        public string? OwnershipMethod { get; set; }
    }

    private sealed class ResolvedRow
    {
        public Guid CompanyId { get; set; }

        public string ClaimantUserId { get; set; } = string.Empty;

        public string[]? RemovedUserIds { get; set; }
    }
}
