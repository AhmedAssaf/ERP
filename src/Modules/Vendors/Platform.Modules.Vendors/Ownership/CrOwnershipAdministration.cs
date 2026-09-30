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
/// sessions), except the organization of a tenant whose staff they are or that their own vendor company works with. The
/// outcome of each step is stored on the dispute and audited, and a failure stays listed in the console until a retry
/// succeeds.
/// </summary>
internal sealed partial class CrOwnershipAdministration(
    IDbContextFactory<VendorsDbContext> contexts,
    IPlatformRequestContext platform,
    ITenantAccessor tenants,
    IVendorAccessor vendors,
    IActingUserAccessor actingUser,
    IPlatformAudit audit,
    IVendorAccounts accounts,
    IStaffTenancies staffTenancies,
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
                r.RegistrantUserId is { } registrant ? await RegistrantAsync(registrant, cancellationToken) : null,
                r.OverCap,
                r.CompanyPending));
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

        var resolved = await ResolveAsync(disputeId, uphold: true, VendorInput.NormalizeFreeText(note), actorId, "vendor.dispute_upheld", cancellationToken);
        if (!resolved.IsSuccess)
        {
            return Result.Failure<CrDisputeUpheld>(resolved.Error);
        }

        var row = resolved.Value;
        var removed = row.RemovedUserIds ?? [];
        var run = await UpdateIdentityProviderAsync(disputeId, row.CompanyId, row.ClaimantUserId, removed, actorId, stored: null, claimantIsAdmin: true);
        return Result.Success(new CrDisputeUpheld(row.CompanyId, row.ClaimantUserId, removed, run.Updated, run.Recorded));
    }

    public async Task<Result<Guid>> RejectAsync(Guid disputeId, string? note, string actorId, CancellationToken cancellationToken = default)
    {
        await RequireActorOfOthersDisputeAsync(disputeId, actorId, cancellationToken);
        if (!VendorInput.IsFreeText(note, OwnershipStore.MaxNoteLength))
        {
            return Result.Failure<Guid>(NoteRequired());
        }

        var resolved = await ResolveAsync(disputeId, uphold: false, VendorInput.NormalizeFreeText(note), actorId, "vendor.dispute_rejected", cancellationToken);
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

    /// <summary>
    /// Reruns the failed steps of an upheld dispute's identity provider update. When the company has moved on meanwhile (a
    /// later dispute of the company was upheld, whoever holds it now, or the claimant is no longer its vendor admin:
    /// <c>vendor.dispute_claimant_is_admin</c>, migrations 0025 and 0026), the retry is superseded: the old claimant is
    /// given nothing, but the removed users' failed removals still run, since a later uphold removes only the company's
    /// vendor users of its own time and so never reaches the users this uphold removed (ADR-0013 point 5). The dispute
    /// leaves the list as superseded once none of those removals fails. A retry that finds the dispute already marked
    /// superseded by a parallel retry answers superseded too, and never writes its own outcome over it (migration 0027).
    /// </summary>
    public async Task<CrDisputeRetry> RetryIdentityProviderAsync(Guid disputeId, string actorId, CancellationToken cancellationToken = default)
    {
        RequireActor(actorId);
        var failure = (await FailureRowsAsync(cancellationToken)).SingleOrDefault(r => r.Id == disputeId)
            ?? throw new InvalidOperationException("Only an upheld dispute whose identity provider update failed is retried.");
        var claimantIsAdmin = await ClaimantIsAdminAsync(disputeId, cancellationToken);
        var run = await UpdateIdentityProviderAsync(
            disputeId, failure.CompanyId, failure.ClaimantUserId, failure.RemovedUserIds ?? [], actorId, stored: Steps(failure.IdpDetails) ?? [], claimantIsAdmin);
        if (run.Superseded && run.Recorded && run.Updated)
        {
            SupersededLog(logger, disputeId);
            return CrDisputeRetry.Superseded;
        }

        return run.Updated && run.Recorded ? CrDisputeRetry.Updated : CrDisputeRetry.StillFailing;
    }

    private async Task<bool> ClaimantIsAdminAsync(Guid disputeId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.Database.SqlQuery<bool>($"select vendor.dispute_claimant_is_admin({disputeId}) as \"Value\"").SingleAsync(cancellationToken);
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
    /// The identity provider side of an uphold: the claimant gets the realm role <c>vendor</c> and membership of the
    /// organization of every tenant the company worked with when the uphold committed, so the Vendor policy opens for them
    /// at their next sign-in on each of those hosts (W-21's <c>/vendor/join</c> refuses to restore a membership, P-1); every
    /// removed user leaves those organizations and loses the role (W-21 then ends their open sessions). Each step's outcome
    /// is stored on the dispute (<c>idp_details</c>) with the overall one and audited as
    /// <c>vendor.dispute_identity_provider</c>; a failing step is logged and never stops the others.
    /// A retry passes the <paramref name="stored"/> outcomes: a step already done or skipped is kept and never run again,
    /// so a membership a tenant took away after the uphold stays taken away (only the tenant restores access, W-21 P-1);
    /// only the failed steps run, and a failed step a retry no longer reaches (its tenant gone from the company) is
    /// recorded as skipped, but only when the tenant lookup succeeded in that run: while it fails, the steps stay failed.
    /// Without stored outcomes (the uphold itself) every step runs.
    /// A grant or add that answers "already there" is recorded as <see cref="StepAlreadyThere"/>: done for a retry, but
    /// never taken back, since this run did not give it.
    /// Before a removed user's (or an undo's) steps run, their standing is read (<see cref="StandingAsync"/>): the
    /// organization of a tenant whose staff they are (invited or active) or that their current vendor company works with
    /// is skipped with the reason, and the realm role stays with a user who belongs to a vendor company again; the other
    /// organizations are still taken. When the standing cannot be read, those steps are recorded as failed and run on a
    /// retry.
    /// When <paramref name="claimantIsAdmin"/> is false (the company moved on to a later dispute), the claimant is given
    /// nothing and only the removals run; the outcome is then recorded as superseded once none failed. After granting
    /// anything, the claimant is checked again: a later uphold that committed while the steps ran removed them, so what this
    /// run granted is taken back (<c>undo:</c> steps) and the outcome is superseded.
    /// </summary>
    private async Task<IdentityProviderRun> UpdateIdentityProviderAsync(
        Guid disputeId, Guid companyId, string claimant, IReadOnlyList<string> removed, string actorId,
        IReadOnlyDictionary<string, string>? stored, bool claimantIsAdmin)
    {
        var retry = stored is not null;
        var earlier = stored ?? new Dictionary<string, string>();
        var steps = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (step, outcome) in earlier.Where(e => e.Value != StepFailed))
        {
            steps[step] = outcome;
        }

        var organizations = await RelatedOrganizationsAsync(disputeId, steps);
        var superseded = !claimantIsAdmin;
        var undo = new List<string>();
        if (claimantIsAdmin)
        {
            var granted = new List<string>();
            if (await StepAsync(steps, GrantStep, disputeId, claimant, () => accounts.GrantRoleAsync(claimant, CancellationToken.None)))
            {
                granted.Add(GrantStep);
            }

            foreach (var (_, alias) in organizations.Related)
            {
                if (await StepAsync(steps, AddPrefix + alias, disputeId, claimant, () => accounts.AddToOrganizationAsync(claimant, alias, CancellationToken.None)))
                {
                    granted.Add(AddPrefix + alias);
                }
            }

            if (granted.Count > 0 || earlier.GetValueOrDefault(RecheckStep) == StepFailed)
            {
                // When the check itself fails (null), what this run granted stays recorded as done and the failed check
                // keeps the dispute listed; a retry that then finds the company moved on takes it back (below).
                if (await StillAdminAsync(disputeId, steps) == false)
                {
                    MovedDuringRunLog(logger, disputeId, granted.Count);
                    superseded = true;
                    foreach (var step in granted)
                    {
                        steps.Remove(UndoPrefix + step);
                    }

                    undo.AddRange(granted);
                }
            }
        }
        else
        {
            // Superseded: the claimant is given nothing any more.
            foreach (var step in earlier.Where(e => e.Value == StepFailed && IsGrant(e.Key)).Select(e => e.Key))
            {
                steps[step] = StepSkippedClaimantMovedOn;
            }

            // What an earlier run could not take back, and, if that run could not check the claimant again, everything it
            // granted.
            undo.AddRange(earlier.Where(e => e.Value == StepFailed && e.Key.StartsWith(UndoPrefix, StringComparison.Ordinal)).Select(e => e.Key[UndoPrefix.Length..]));
            if (earlier.GetValueOrDefault(RecheckStep) == StepFailed)
            {
                undo.AddRange(earlier.Where(e => e.Value == StepDone && IsGrant(e.Key)).Select(e => e.Key));
                steps[RecheckStep] = StepDone;
            }
        }

        await UndoAsync(steps, disputeId, claimant, undo, organizations);
        await RemoveAsync(steps, disputeId, claimant, removed, organizations);

        // A failed step this run did not reach again: its tenant no longer counts (or, for the lookup, it succeeded now).
        // Only a lookup that succeeded in this run shows that; while it fails, no organization step was reached, so the
        // failed ones stay failed for the next retry.
        foreach (var step in earlier.Where(e => e.Value == StepFailed && e.Key != LookupStep).Select(e => e.Key))
        {
            steps.TryAdd(step, organizations.LookupFailed ? StepFailed : StepSkippedNoLongerApplies);
        }

        var updated = steps.Values.All(v => v != StepFailed);

        // Recorded after the fact on the committed dispute. If recording fails, the dispute keeps its earlier recorded
        // outcome (or none), so it stays listed for a retry, and the admin is told so; the uphold itself stands.
        var data = new Dictionary<string, string?>
        {
            ["outcome"] = !updated ? "failed" : superseded ? "superseded" : "updated",
            ["retry"] = retry ? "true" : "false",
        };
        if (superseded)
        {
            data["company_id"] = companyId.ToString();
            data["claimant"] = claimant;
            data["reason"] = SupersededReason;
        }

        foreach (var (step, outcome) in steps)
        {
            data[step] = outcome;
        }

        try
        {
            var details = System.Text.Json.JsonSerializer.Serialize(steps);
            await using var db = await contexts.CreateDbContextAsync(CancellationToken.None);

            // The outcome is written first, in a transaction, so the entry carries what the dispute really got (F-41), and
            // it commits only after its entry was written. A superseded run marks it only while the dispute is still
            // superseded and still needs its outcome (migrations 0025, 0026); any other run never replaces a superseded
            // outcome (migration 0027). Either write matches nothing when a parallel retry got there first.
            var supersedeRun = superseded && updated;
            await using var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None);
            var written = supersedeRun
                ? await db.Database.SqlQuery<bool>(
                    $"select vendor.supersede_dispute_idp({disputeId}, {details}::jsonb) as \"Value\"").SingleAsync(CancellationToken.None)
                : await db.Database.SqlQuery<bool>(
                    $"select vendor.record_dispute_idp_outcome({disputeId}, {updated}, {details}::jsonb) as \"Value\"").SingleAsync(CancellationToken.None);

            // Not written: read what the dispute holds now. When a parallel retry marked it superseded, so is this run: that
            // mark needs a run in which no step failed, so the dispute is settled and leaves the list.
            var supersededMeanwhile = !written && await db.Database.SqlQuery<string?>(
                $"select vendor.dispute_idp_outcome({disputeId}) as \"Value\"").SingleAsync(CancellationToken.None) == "superseded";
            if (!written)
            {
                data["outcome"] = supersedeRun ? "superseded_not_marked" : supersededMeanwhile ? "not_recorded_superseded" : "not_recorded";
            }

            await audit.WriteAsync(new PlatformAuditEntry(actorId, "vendor.dispute_identity_provider", "cr_dispute", disputeId.ToString(), data), CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
            if (supersededMeanwhile)
            {
                SupersededMeanwhileLog(logger, disputeId);
                return new IdentityProviderRun(Updated: true, Recorded: true, Superseded: true);
            }

            if (!written)
            {
                OutcomeNotWrittenLog(logger, disputeId);
            }

            return new IdentityProviderRun(updated, written, superseded);
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException or TimeoutException)
        {
            OutcomeNotRecorded(logger, disputeId, ex.GetType().Name);
            return new IdentityProviderRun(updated, Recorded: false, superseded);
        }
    }

    /// <summary>
    /// The removed users' steps: each leaves the uphold's organizations and loses the realm role, except where their
    /// standing says otherwise (<see cref="SkipReason"/>, recorded as skipped with the reason, for good): the organization
    /// of a tenant whose staff they are or that their current vendor company works with stays, and so does the role of a
    /// user who belongs to a vendor company again. When the standing cannot be read, the user's pending steps are recorded
    /// as failed and run on a retry.
    /// </summary>
    private async Task RemoveAsync(
        SortedDictionary<string, string> steps, Guid disputeId, string claimant, IReadOnlyList<string> removed, Organizations organizations)
    {
        foreach (var user in removed.Where(u => !string.Equals(u, claimant, StringComparison.Ordinal)))
        {
            var pending = organizations.Related
                .Select(o => (o.TenantId, o.Alias, Step: $"organization:remove:{o.Alias}:{user}"))
                .Where(o => !steps.ContainsKey(o.Step))
                .ToList();
            var revoke = $"role:revoke:{user}";
            var revokePending = !steps.ContainsKey(revoke);
            if (pending.Count == 0 && !revokePending)
            {
                continue;
            }

            if (await StandingAsync(disputeId, user) is not { } standing)
            {
                foreach (var (_, _, step) in pending)
                {
                    steps[step] = StepFailed;
                }

                if (revokePending)
                {
                    steps[revoke] = StepFailed;
                }

                continue;
            }

            foreach (var (tenantId, alias, step) in pending)
            {
                if (SkipReason(standing, tenantId) is { } reason)
                {
                    steps[step] = reason;
                    continue;
                }

                await StepAsync(steps, step, disputeId, user,
                    () => accounts.RevokeAsync(new VendorAccessGrant(user, alias, RoleAdded: false, OrganizationAdded: true), CancellationToken.None));
            }

            if (revokePending && standing.IsVendor)
            {
                steps[revoke] = StepSkippedVendorAgain;
            }
            else if (revokePending)
            {
                await StepAsync(steps, revoke, disputeId, user,
                    () => accounts.RevokeAsync(new VendorAccessGrant(user, string.Empty, RoleAdded: true, OrganizationAdded: false), CancellationToken.None));
            }
        }
    }

    /// <summary>
    /// Takes back from the claimant what <paramref name="granted"/> names (<c>role:grant</c>, <c>organization:add:*</c>),
    /// each as an <c>undo:</c> step, under the same standing rules as a removal (<see cref="RemoveAsync"/>). An
    /// organization whose tenant cannot be told because the tenant lookup failed in this run stays failed for a retry; one
    /// the catalog no longer lists has no staff or vendor relationship left to keep, so it is taken back.
    /// </summary>
    private async Task UndoAsync(
        SortedDictionary<string, string> steps, Guid disputeId, string claimant, IReadOnlyList<string> granted, Organizations organizations)
    {
        var pending = granted.Distinct(StringComparer.Ordinal).Select(g => UndoPrefix + g).Where(s => !steps.ContainsKey(s)).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        if (await StandingAsync(disputeId, claimant) is not { } standing)
        {
            foreach (var step in pending)
            {
                steps[step] = StepFailed;
            }

            return;
        }

        foreach (var step in pending)
        {
            var grant = step[UndoPrefix.Length..];
            VendorAccessGrant access;
            if (grant == GrantStep)
            {
                if (standing.IsVendor)
                {
                    steps[step] = StepSkippedVendorAgain;
                    continue;
                }

                access = new VendorAccessGrant(claimant, string.Empty, RoleAdded: true, OrganizationAdded: false);
            }
            else
            {
                var alias = grant[AddPrefix.Length..];
                if (organizations.TenantByAlias.TryGetValue(alias, out var tenantId) && SkipReason(standing, tenantId) is { } reason)
                {
                    steps[step] = reason;
                    continue;
                }

                if (organizations.LookupFailed)
                {
                    steps[step] = StepFailed;
                    continue;
                }

                access = new VendorAccessGrant(claimant, alias, RoleAdded: false, OrganizationAdded: true);
            }

            await StepAsync(steps, step, disputeId, claimant, () => accounts.RevokeAsync(access, CancellationToken.None));
        }
    }

    /// <summary>
    /// What a removal or undo must not take from the user: the tenants they are staff of (invited or active, through the
    /// Identity module's <see cref="IStaffTenancies"/>), and the vendor company they belong to now with the tenants it works
    /// with (<c>vendor.user_company_tenants</c>, migration 0026). Null, logged, when either cannot be read.
    /// </summary>
    private async Task<Standing?> StandingAsync(Guid disputeId, string userId)
    {
        try
        {
            await using var db = await contexts.CreateDbContextAsync(CancellationToken.None);
            var rows = await db.Database.SqlQuery<CompanyTenantRow>(
                $"select company_id, tenant_id from vendor.user_company_tenants({userId})").ToListAsync(CancellationToken.None);
            var staff = await staffTenancies.TenantsOfAsync(userId, CancellationToken.None);
            return new Standing(rows.Count > 0, rows.Where(r => r.TenantId is not null).Select(r => r.TenantId!.Value).ToHashSet(), staff);
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException or TimeoutException)
        {
            StepFailedLog(logger, disputeId, "standing:lookup", userId, ex.GetType().Name);
            return null;
        }
    }

    /// <summary>Why the user keeps the tenant's organization, or null when a removal (or undo) takes it.</summary>
    private static string? SkipReason(Standing standing, Guid tenantId) =>
        standing.StaffTenants.Contains(tenantId) ? StepSkippedStaff
        : standing.VendorTenants.Contains(tenantId) ? StepSkippedVendorTenant
        : null;

    /// <summary>
    /// Whether the claimant is still the company's vendor admin after this run's grants (<see cref="RecheckStep"/> done), or
    /// null when that could not be checked (the step failed, so the dispute stays listed).
    /// </summary>
    private async Task<bool?> StillAdminAsync(Guid disputeId, SortedDictionary<string, string> steps)
    {
        try
        {
            var admin = await ClaimantIsAdminAsync(disputeId, CancellationToken.None);
            steps[RecheckStep] = StepDone;
            return admin;
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException or TimeoutException)
        {
            StepFailedLog(logger, disputeId, RecheckStep, string.Empty, ex.GetType().Name);
            steps[RecheckStep] = StepFailed;
            return null;
        }
    }

    private static bool IsGrant(string step) => step == GrantStep || step.StartsWith(AddPrefix, StringComparison.Ordinal);

    private readonly record struct IdentityProviderRun(bool Updated, bool Recorded, bool Superseded);

    /// <summary>
    /// The organizations of the tenants the dispute's company worked with at the uphold (<see cref="Related"/>), every
    /// catalog tenant by its organization alias, and whether the lookup failed in this run (both are then empty).
    /// </summary>
    private sealed record Organizations(
        IReadOnlyList<(Guid TenantId, string Alias)> Related, IReadOnlyDictionary<string, Guid> TenantByAlias, bool LookupFailed);

    /// <summary>A user's standing when a removal or undo runs (<see cref="StandingAsync"/>).</summary>
    private sealed record Standing(bool IsVendor, IReadOnlySet<Guid> VendorTenants, IReadOnlySet<Guid> StaffTenants);

    private const string GrantStep = "role:grant";

    private const string AddPrefix = "organization:add:";

    /// <summary>A step taking back from the claimant what this dispute's update granted: <c>undo:role:grant</c>, <c>undo:organization:add:{alias}</c>.</summary>
    private const string UndoPrefix = "undo:";

    /// <summary>The check, after the grants, that the claimant is still the company's vendor admin.</summary>
    private const string RecheckStep = "claimant:recheck";

    /// <summary>A role revoke (or its undo) not run because the user belongs to a vendor company again; final, never counted as failed.</summary>
    private const string StepSkippedVendorAgain = "skipped: the user belongs to a vendor company again";

    /// <summary>A removal (or undo) from the organization of a tenant the user's current vendor company works with; final.</summary>
    private const string StepSkippedVendorTenant = "skipped: the tenant works with the user's vendor company";

    /// <summary>A removal (or undo) from the organization of a tenant whose staff the user is (invited or active); final.</summary>
    private const string StepSkippedStaff = "skipped: the user is the tenant's staff";

    /// <summary>A grant or add that found the access already there: done for a retry, never taken back by an undo.</summary>
    private const string StepAlreadyThere = "done: already there";

    /// <summary>Why a superseded run gives the claimant nothing (audited as its reason).</summary>
    private const string SupersededReason = "a later upheld dispute of the company decides its access";

    /// <summary>A grant that failed earlier and is not run once the dispute is superseded; final.</summary>
    private const string StepSkippedClaimantMovedOn = "skipped: " + SupersededReason;

    private const string StepDone = "done";

    private const string StepFailed = "failed";

    /// <summary>A step that could not apply and will not on a retry either, with its reason; never counted as failed.</summary>
    private const string StepSkippedNotInCatalog = "skipped: the tenant is not in the tenant catalog";

    /// <summary>A step that failed earlier and that a retry no longer reaches, with its reason; never counted as failed.</summary>
    private const string StepSkippedNoLongerApplies = "skipped: no longer among the tenants of the uphold";

    private const string LookupStep = "organization:lookup";

    /// <summary>
    /// The Keycloak organizations of the tenants the dispute's company works with, and every catalog tenant by alias; a
    /// failed lookup is a failed step.
    /// </summary>
    private async Task<Organizations> RelatedOrganizationsAsync(Guid disputeId, SortedDictionary<string, string> steps)
    {
        try
        {
            await using var db = await contexts.CreateDbContextAsync(CancellationToken.None);
            var tenantIds = await db.Database.SqlQuery<Guid>(
                $"select tenant_id as \"Value\" from vendor.dispute_related_tenants({disputeId})").ToListAsync(CancellationToken.None);
            var catalog = await tenantCatalog.ListAsync(CancellationToken.None);
            var byId = catalog.ToDictionary(t => t.Id, t => t.OrganizationAlias);
            var related = new List<(Guid TenantId, string Alias)>(tenantIds.Count);
            foreach (var tenantId in tenantIds)
            {
                if (byId.TryGetValue(tenantId, out var alias))
                {
                    related.Add((tenantId, alias));
                    steps.Remove($"{LookupStep}:{tenantId:D}");
                }
                else
                {
                    // A tenant the catalog no longer lists has no organization to join; a retry would fail again forever.
                    steps[$"{LookupStep}:{tenantId:D}"] = StepSkippedNotInCatalog;
                }
            }

            return new Organizations(related, catalog.ToDictionary(t => t.OrganizationAlias, t => t.Id, StringComparer.Ordinal), LookupFailed: false);
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException or TimeoutException)
        {
            StepFailedLog(logger, disputeId, LookupStep, string.Empty, ex.GetType().Name);
            steps[LookupStep] = StepFailed;
            return new Organizations([], new Dictionary<string, Guid>(StringComparer.Ordinal), LookupFailed: true);
        }
    }

    /// <summary>
    /// One identity provider step; true when this run did it. A step with an outcome other than failed (kept from an earlier
    /// run: done, already there, or skipped with its reason) is not run again. A step that throws the provider's failure, or
    /// answers false for a removal (which reports whether it succeeded), is recorded as failed and logged; a grant or an add
    /// answering false found the access already there (<see cref="StepAlreadyThere"/>), which is not this run's doing.
    /// </summary>
    private async Task<bool> StepAsync(SortedDictionary<string, string> steps, string step, Guid disputeId, string userId, Func<Task<bool>> run)
    {
        if (steps.TryGetValue(step, out var earlier) && earlier != StepFailed)
        {
            return false;
        }

        try
        {
            var answer = await run();
            var removal = step.StartsWith("organization:remove:", StringComparison.Ordinal)
                || step.StartsWith("role:revoke:", StringComparison.Ordinal)
                || step.StartsWith(UndoPrefix, StringComparison.Ordinal);
            steps[step] = answer ? StepDone : removal ? StepFailed : StepAlreadyThere;
            return answer;
        }
        catch (Exception ex) when (ex is IdentityProviderException or InvalidOperationException)
        {
            StepFailedLog(logger, disputeId, step, userId, ex.GetType().Name);
            steps[step] = StepFailed;
            return false;
        }
    }

    private static IReadOnlyList<string> FailedSteps(string? details) =>
        [.. (Steps(details) ?? []).Where(s => s.Value == StepFailed).Select(s => s.Key).Order(StringComparer.Ordinal)];

    /// <summary>The stored outcome of each step, or null when none was recorded.</summary>
    private static Dictionary<string, string>? Steps(string? details) =>
        string.IsNullOrWhiteSpace(details) ? null : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(details) ?? [];

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
                   raised_on_tenant, raised_at, registrant_user_id, ownership_method, status, over_cap, company_pending
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Dispute {DisputeId} was upheld, but the identity provider outcome could not be recorded ({ErrorType}); it stays listed for a retry.")]
    private static partial void OutcomeNotRecorded(ILogger logger, Guid disputeId, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The retry of dispute {DisputeId} gave its claimant nothing: a later upheld dispute of the company decides its access; the removed users' steps ran and it is recorded as superseded.")]
    private static partial void SupersededLog(ILogger logger, Guid disputeId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The claimant of dispute {DisputeId} lost the company while its identity provider update ran; the {Count} grants of this run are taken back.")]
    private static partial void MovedDuringRunLog(ILogger logger, Guid disputeId, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The outcome of dispute {DisputeId} was not written: its claimant is the company's vendor admin again, or its outcome changed meanwhile; it stays listed.")]
    private static partial void OutcomeNotWrittenLog(ILogger logger, Guid disputeId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A parallel retry marked dispute {DisputeId} superseded while this run's steps ran; this run's outcome is not written over it.")]
    private static partial void SupersededMeanwhileLog(ILogger logger, Guid disputeId);

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

        public bool OverCap { get; set; }

        public int CompanyPending { get; set; }
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

    private sealed class CompanyTenantRow
    {
        public Guid CompanyId { get; set; }

        public Guid? TenantId { get; set; }
    }

    private sealed class ResolvedRow
    {
        public Guid CompanyId { get; set; }

        public string ClaimantUserId { get; set; } = string.Empty;

        public string[]? RemovedUserIds { get; set; }

        public int ClosedOtherDisputes { get; set; }
    }
}
