using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Operations.Contracts;
using Platform.Modules.Tenancy.Contracts;
using Platform.Modules.Vendors.Contracts;
using Platform.Modules.Vendors.Persistence;
using Platform.Modules.Vendors.Registration;
using Platform.Shared.Results;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Vendors.Ownership;

/// <summary>
/// The platform console's side of W-33 (<see cref="ICrOwnershipAdministration"/>): the check method and the review of
/// disputes. Every call needs a platform request (the console's scope) without a tenant or vendor context, and changes
/// run as the acting user; the database functions refuse any other session themselves (migrations 0018, 0019), and a
/// platform admin never handles a dispute they raised (L-3, here and in the database). Each change writes its platform
/// audit entry on its own connection before the change commits, so a change is never committed without its entry; the
/// two are not atomic, so a commit that fails after the entry was written leaves an entry for a change that did not
/// happen (the same trade-off as the approval's audit in <c>VendorDirectory</c>). After an upheld dispute commits, the
/// identity provider is updated: the claimant gets the realm role <c>vendor</c> and membership of the organization of
/// every tenant the company works with, the removed users lose the role and those memberships (W-21 then ends their open
/// sessions). The outcome of each step is stored on the dispute and audited, and a failure stays listed in the console
/// until a retry succeeds.
/// </summary>
internal sealed partial class CrOwnershipAdministration(
    IDbContextFactory<VendorsDbContext> contexts,
    IPlatformRequestContext platform,
    ITenantAccessor tenants,
    IVendorAccessor vendors,
    IActingUserAccessor actingUser,
    IPlatformAudit audit,
    IVendorAccounts accounts,
    ITenantCatalog tenantCatalog,
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
        if (string.Equals(before, code, StringComparison.Ordinal))
        {
            // Saving the method it already is changes nothing and is not audited; the transaction rolls back.
            return Result.Success(method);
        }

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
        var rows = await OpenRowsAsync(cancellationToken);
        var disputes = new List<CrDispute>(rows.Count);
        foreach (var r in rows)
        {
            disputes.Add(new CrDispute(
                r.Id, r.CompanyId, r.CrNumber, r.NameAr, r.NameEn, r.ClaimantUserId, r.ClaimantEmail, r.ClaimantName, r.Statement,
                r.RaisedOnTenant, r.RaisedAt, r.RegistrantUserId, OwnershipStore.VerificationMethod(r.OwnershipMethod),
                r.Status == "under_review" ? CrDisputeStatus.UnderReview : CrDisputeStatus.Open,
                r.RegistrantUserId is { } registrant ? await RegistrantAsync(registrant, cancellationToken) : null));
        }

        return disputes;
    }

    public async Task<Result<Guid>> AcceptForReviewAsync(Guid disputeId, string actorId, CancellationToken cancellationToken = default)
    {
        await RequireActorOfOthersDisputeAsync(disputeId, actorId, cancellationToken);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var company = await db.Database.SqlQuery<Guid?>($"select vendor.accept_cr_dispute({disputeId}) as \"Value\"").SingleAsync(cancellationToken);
        if (company is not { } companyId)
        {
            return Result.Failure<Guid>(NotOpen());
        }

        await audit.WriteAsync(
            new PlatformAuditEntry(actorId, "vendor.dispute_accepted", "cr_dispute", disputeId.ToString(), new Dictionary<string, string?>
            {
                ["company_id"] = companyId.ToString(),
            }),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Result.Success(disputeId);
    }

    public async Task<Result<CrDisputeUpheld>> UpholdAsync(Guid disputeId, string? note, string actorId, CancellationToken cancellationToken = default)
    {
        await RequireActorOfOthersDisputeAsync(disputeId, actorId, cancellationToken);
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
        var updated = await UpdateIdentityProviderAsync(disputeId, row.ClaimantUserId, removed, actorId, retry: false);
        return Result.Success(new CrDisputeUpheld(row.CompanyId, row.ClaimantUserId, removed, updated));
    }

    public async Task<Result<Guid>> RejectAsync(Guid disputeId, string? note, string actorId, CancellationToken cancellationToken = default)
    {
        await RequireActorOfOthersDisputeAsync(disputeId, actorId, cancellationToken);
        if (!VendorInput.IsFreeText(note, OwnershipStore.MaxNoteLength))
        {
            return Result.Failure<Guid>(NoteRequired());
        }

        var resolved = await ResolveAsync(disputeId, uphold: false, note!.Trim(), actorId, "vendor.dispute_rejected", cancellationToken);
        return resolved.IsSuccess ? Result.Success(disputeId) : Result.Failure<Guid>(resolved.Error);
    }

    public async Task<IReadOnlyList<CrDisputeIdentityProviderFailure>> ListIdentityProviderFailuresAsync(CancellationToken cancellationToken = default)
    {
        RequirePlatform();
        var rows = await FailureRowsAsync(cancellationToken);
        return
        [
            .. rows.Select(r => new CrDisputeIdentityProviderFailure(
                r.Id, r.CompanyId, r.CrNumber, r.NameAr, r.NameEn, r.ClaimantUserId, r.ClaimantName, r.RemovedUserIds ?? [], r.ResolvedAt,
                FailedSteps(r.IdpDetails))),
        ];
    }

    public async Task<bool> RetryIdentityProviderAsync(Guid disputeId, string actorId, CancellationToken cancellationToken = default)
    {
        RequireActor(actorId);
        var failure = (await FailureRowsAsync(cancellationToken)).SingleOrDefault(r => r.Id == disputeId)
            ?? throw new InvalidOperationException("Only an upheld dispute whose identity provider update failed is retried.");
        return await UpdateIdentityProviderAsync(disputeId, failure.ClaimantUserId, failure.RemovedUserIds ?? [], actorId, retry: true);
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
                select company_id, claimant_user_id, removed_user_ids, closed_other_disputes
                from vendor.resolve_cr_dispute({disputeId}, {uphold}, {note})
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
            return Result.Failure<ResolvedRow>(NotOpen());
        }

        await audit.WriteAsync(
            new PlatformAuditEntry(actorId, action, "cr_dispute", disputeId.ToString(), new Dictionary<string, string?>
            {
                ["company_id"] = row.CompanyId.ToString(),
                ["claimant"] = row.ClaimantUserId,
                ["removed_users"] = row.RemovedUserIds is null ? null : string.Join(',', row.RemovedUserIds),
                ["closed_other_disputes"] = uphold ? row.ClosedOtherDisputes.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
                ["note"] = note,
            }),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Result.Success(row);
    }

    /// <summary>
    /// The identity provider side of an uphold, every step idempotent so a retry repeats all of them: the claimant gets the
    /// realm role <c>vendor</c> and membership of the organization of every tenant the company works with, so the Vendor
    /// policy opens for them at their next sign-in on each of those hosts (W-21's <c>/vendor/join</c> refuses to restore a
    /// membership, P-1); every removed user leaves those organizations and loses the role (W-21 then ends their open
    /// sessions). Each step's outcome is stored on the dispute (<c>idp_details</c>) with the overall one and audited as
    /// <c>vendor.dispute_identity_provider</c>; a failing step is logged and never stops the others.
    /// </summary>
    private async Task<bool> UpdateIdentityProviderAsync(Guid disputeId, string claimant, IReadOnlyList<string> removed, string actorId, bool retry)
    {
        var steps = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var aliases = await RelatedOrganizationsAsync(disputeId, steps);

        await StepAsync(steps, "role:grant", disputeId, claimant, () => accounts.GrantRoleAsync(claimant, CancellationToken.None));
        foreach (var alias in aliases)
        {
            await StepAsync(steps, $"organization:add:{alias}", disputeId, claimant,
                () => accounts.AddToOrganizationAsync(claimant, alias, CancellationToken.None));
        }

        foreach (var user in removed.Where(u => !string.Equals(u, claimant, StringComparison.Ordinal)))
        {
            foreach (var alias in aliases)
            {
                await StepAsync(steps, $"organization:remove:{alias}:{user}", disputeId, user,
                    () => accounts.RevokeAsync(new VendorAccessGrant(user, alias, RoleAdded: false, OrganizationAdded: true), CancellationToken.None));
            }

            await StepAsync(steps, $"role:revoke:{user}", disputeId, user,
                () => accounts.RevokeAsync(new VendorAccessGrant(user, string.Empty, RoleAdded: true, OrganizationAdded: false), CancellationToken.None));
        }

        var updated = steps.Values.All(v => v == StepDone);

        // Recorded after the fact on the committed dispute; if recording itself fails, the dispute stays listed as
        // needing the identity provider (its outcome is still unknown), which is the safe side.
        var details = System.Text.Json.JsonSerializer.Serialize(steps);
        await using var db = await contexts.CreateDbContextAsync(CancellationToken.None);
        await db.Database.SqlQuery<bool>(
            $"select vendor.record_dispute_idp_outcome({disputeId}, {updated}, {details}::jsonb) as \"Value\"").SingleAsync(CancellationToken.None);
        var data = new Dictionary<string, string?>
        {
            ["outcome"] = updated ? "updated" : "failed",
            ["retry"] = retry ? "true" : "false",
        };
        foreach (var (step, outcome) in steps)
        {
            data[step] = outcome;
        }

        await audit.WriteAsync(new PlatformAuditEntry(actorId, "vendor.dispute_identity_provider", "cr_dispute", disputeId.ToString(), data), CancellationToken.None);
        return updated;
    }

    private const string StepDone = "done";

    private const string StepFailed = "failed";

    /// <summary>The Keycloak organization aliases of the tenants the dispute's company works with; a failed lookup is a failed step.</summary>
    private async Task<IReadOnlyList<string>> RelatedOrganizationsAsync(Guid disputeId, SortedDictionary<string, string> steps)
    {
        try
        {
            await using var db = await contexts.CreateDbContextAsync(CancellationToken.None);
            var tenantIds = await db.Database.SqlQuery<Guid>(
                $"select tenant_id as \"Value\" from vendor.dispute_related_tenants({disputeId})").ToListAsync(CancellationToken.None);
            var byId = (await tenantCatalog.ListAsync(CancellationToken.None)).ToDictionary(t => t.Id, t => t.OrganizationAlias);
            var aliases = new List<string>(tenantIds.Count);
            foreach (var tenantId in tenantIds)
            {
                if (byId.TryGetValue(tenantId, out var alias))
                {
                    aliases.Add(alias);
                }
                else
                {
                    steps[$"organization:lookup:{tenantId:D}"] = StepFailed;
                }
            }

            return aliases;
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException or TimeoutException)
        {
            StepFailedLog(logger, disputeId, "organization:lookup", string.Empty, ex.GetType().Name);
            steps["organization:lookup"] = StepFailed;
            return [];
        }
    }

    /// <summary>
    /// One identity provider step. A step that throws the provider's failure, or answers false for a removal (which reports
    /// whether it succeeded), is recorded as failed and logged; a grant or an add answering false means "already there".
    /// </summary>
    private async Task StepAsync(SortedDictionary<string, string> steps, string step, Guid disputeId, string userId, Func<Task<bool>> run)
    {
        try
        {
            var answer = await run();
            var removal = step.StartsWith("organization:remove:", StringComparison.Ordinal) || step.StartsWith("role:revoke:", StringComparison.Ordinal);
            steps[step] = removal && !answer ? StepFailed : StepDone;
        }
        catch (Exception ex) when (ex is IdentityProviderException or InvalidOperationException)
        {
            StepFailedLog(logger, disputeId, step, userId, ex.GetType().Name);
            steps[step] = StepFailed;
        }
    }

    private static IReadOnlyList<string> FailedSteps(string? details)
    {
        if (string.IsNullOrWhiteSpace(details))
        {
            return [];
        }

        var steps = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(details) ?? [];
        return [.. steps.Where(s => s.Value == StepFailed).Select(s => s.Key).Order(StringComparer.Ordinal)];
    }

    private async Task<VendorRegistrant> RegistrantAsync(string userId, CancellationToken cancellationToken)
    {
        try
        {
            return OwnershipStore.Registrant(userId, await accounts.ProfileAsync(userId, cancellationToken));
        }
        catch (Exception ex) when (ex is IdentityProviderException or InvalidOperationException)
        {
            RegistrantUnknown(logger, userId, ex.GetType().Name);
            return new VendorRegistrant(userId, null, null);
        }
    }

    private async Task<List<OpenDisputeRow>> OpenRowsAsync(CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.Database.SqlQuery<OpenDisputeRow>($"""
            select id, company_id, cr_number, name_ar, name_en, claimant_user_id, claimant_email, claimant_name, statement,
                   raised_on_tenant, raised_at, registrant_user_id, ownership_method, status
            from vendor.open_cr_disputes()
            """).ToListAsync(cancellationToken);
    }

    private async Task<List<FailureRow>> FailureRowsAsync(CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.Database.SqlQuery<FailureRow>($"""
            select id, company_id, cr_number, name_ar, name_en, claimant_user_id, claimant_name, removed_user_ids, resolved_at,
                   idp_details::text as idp_details
            from vendor.upheld_disputes_needing_idp()
            """).ToListAsync(cancellationToken);
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

    /// <summary>L-3: as <see cref="RequireActor"/>, and the dispute is not the admin's own (the database refuses it too).</summary>
    private async Task RequireActorOfOthersDisputeAsync(Guid disputeId, string actorId, CancellationToken cancellationToken)
    {
        RequireActor(actorId);
        var own = (await OpenRowsAsync(cancellationToken)).Any(r => r.Id == disputeId && string.Equals(r.ClaimantUserId, actorId, StringComparison.Ordinal));
        if (own)
        {
            throw new InvalidOperationException("A platform admin never handles a dispute they raised.");
        }
    }

    private static Error NoteRequired() =>
        Error.Validation(CrDisputeErrors.NoteRequired, $"Write a note of up to {OwnershipStore.MaxNoteLength} characters on what you checked.");

    private static Error NotOpen() => Error.Conflict(CrDisputeErrors.NotOpen, "This dispute is no longer open.");

    [LoggerMessage(Level = LogLevel.Error, Message = "Dispute {DisputeId} was upheld, but the identity provider step {Step} for user {UserId} failed ({ErrorType}); the console offers a retry.")]
    private static partial void StepFailedLog(ILogger logger, Guid disputeId, string step, string userId, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The identity provider did not describe the current vendor admin {UserId} for the console ({ErrorType}).")]
    private static partial void RegistrantUnknown(ILogger logger, string userId, string errorType);

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

        public string Status { get; set; } = string.Empty;
    }

    private sealed class FailureRow
    {
        public Guid Id { get; set; }

        public Guid CompanyId { get; set; }

        public string CrNumber { get; set; } = string.Empty;

        public string NameAr { get; set; } = string.Empty;

        public string NameEn { get; set; } = string.Empty;

        public string ClaimantUserId { get; set; } = string.Empty;

        public string ClaimantName { get; set; } = string.Empty;

        public string[]? RemovedUserIds { get; set; }

        public DateTimeOffset ResolvedAt { get; set; }

        public string? IdpDetails { get; set; }
    }

    private sealed class ResolvedRow
    {
        public Guid CompanyId { get; set; }

        public string ClaimantUserId { get; set; } = string.Empty;

        public string[]? RemovedUserIds { get; set; }

        public int ClosedOtherDisputes { get; set; }
    }
}
