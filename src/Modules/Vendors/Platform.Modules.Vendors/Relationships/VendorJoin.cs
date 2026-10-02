using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Platform.Modules.Audit.Contracts;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Vendors.Access;
using Platform.Modules.Vendors.Contracts;
using Platform.Modules.Vendors.Persistence;
using Platform.Shared.Results;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Vendors.Relationships;

/// <summary>
/// A vendor of another tenant joins the host tenant (spec section 3, ADR-0008). One database transaction opens first and
/// takes the join lock shared (<see cref="JoinLock"/>, W-40); inside it, Keycloak adds the membership of the tenant's
/// organization, which the Vendor policy needs there; then <c>vendor.join_tenant()</c> (a pending relationship for the
/// session's tenant and vendor company, only for a user of that company) answers whether this call created it, and the
/// transaction commits. The audit entry is written before that transaction commits, but on the audit writer's
/// own connection, so it is not part of the transaction: when the audit fails the relationship rolls back; a commit that
/// fails after it leaves an entry for a join that did not happen, and joining again writes a second one. Only the call
/// that created the relationship writes <c>vendor.joined</c>, so two joins at the same moment write one; a call that added
/// the user to the organization while a parallel join created the relationship writes <c>vendor.membership_restored</c>.
/// When the database step fails, the membership this call added is taken back unless a relationship with the tenant exists
/// by then. W-40: the joins of a company to a tenant and that undo are kept apart by <see cref="JoinLock"/>, so the undo
/// never takes back a membership that a parallel join found in Keycloak and then committed its relationship on: either
/// that join commits first and the undo keeps the membership, or it starts after the undo and adds the membership again.
/// A company that already works with the tenant never gets a membership back from here (<see cref="RejoinAsync"/>).
/// </summary>
internal sealed partial class VendorJoin(
    IDbContextFactory<VendorsDbContext> contexts,
    ITenantAccessor tenants,
    IVendorAccessor vendors,
    IActingUserAccessor actingUser,
    IVendorAccounts accounts,
    IAuditWriter audit,
    ILogger<VendorJoin> logger) : IVendorJoin
{
    public async Task<Result<VendorJoined>> JoinAsync(CancellationToken cancellationToken = default)
    {
        var tenant = tenants.Current ?? throw new InvalidOperationException("A vendor joins a tenant on that tenant's host; this scope has none.");
        var vendor = vendors.Current ?? throw new InvalidOperationException("A vendor joins a tenant in its company's vendor context; this scope has none.");
        var userId = actingUser.UserId ?? throw new InvalidOperationException("A vendor joins a tenant as a signed-in user; this scope has none.");

        bool related;
        await using (var db = await contexts.CreateDbContextAsync(cancellationToken))
        {
            if (await VendorUsers.CompanyOfAsync(db, userId, cancellationToken) != vendor.CompanyId)
            {
                throw new InvalidOperationException("A vendor joins a tenant as a user of the company of its vendor context.");
            }

            related = await db.Relationships.AsNoTracking().AnyAsync(r => r.CompanyId == vendor.CompanyId, cancellationToken);
        }

        if (related)
        {
            return await RejoinAsync(tenant, vendor.CompanyId, userId, cancellationToken);
        }

        // W-40: from here to the commit this join holds the join lock shared (JoinLock), so an undo of a parallel failed
        // join cannot take back a membership this join's Keycloak add found and relied on.
        var organizationAdded = false;
        var addingMembership = false;
        try
        {
            await using var db = await contexts.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await JoinLock.ShareAsync(db, tenant.TenantId, vendor.CompanyId, cancellationToken);

            addingMembership = true;
            try
            {
                organizationAdded = await accounts.AddToOrganizationAsync(userId, tenant.KeycloakOrgAlias, cancellationToken);
            }
            catch (IdentityProviderException ex)
            {
                KeycloakFailed(logger, tenant.Slug, userId, ex.InnerException?.GetType().Name ?? ex.GetType().Name);
                return Failed();
            }

            addingMembership = false;
            var created = await db.Database.SqlQuery<bool>($"select vendor.join_tenant() as \"Value\"").SingleAsync(cancellationToken);
            if (created || organizationAdded)
            {
                await audit.WriteAsync(
                    new AuditEntry(
                        userId,
                        created ? "vendor.joined" : "vendor.membership_restored",
                        "vendor_company",
                        vendor.CompanyId.ToString(),
                        new Dictionary<string, string?>
                        {
                            ["organization_added"] = organizationAdded ? "true" : "false",
                        }),
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return Result.Success(new VendorJoined(created, organizationAdded));
        }
        // Any other failure of the Keycloak add propagates as before; the transaction (and the lock) is rolled back by then,
        // as it is here: the undo runs after the using blocks, so it never waits for this join's own shared hold.
        catch (Exception ex) when (!addingMembership && ex is DbException or DbUpdateException or TimeoutException or OperationCanceledException or InvalidOperationException)
        {
            SaveFailed(logger, tenant.Slug, userId, ex.GetType().Name);
            if (organizationAdded)
            {
                await UndoAsync(tenant, vendor.CompanyId, userId);
            }

            if (ex is OperationCanceledException)
            {
                throw;
            }

            return Failed();
        }
    }

    /// <summary>
    /// Takes back the organization membership this call added, unless the company has a relationship with the tenant by
    /// now. The re-check and the revoke run under the join lock held exclusive (W-40, <see cref="JoinLock"/>): the undo first
    /// waits for every join of the company to the tenant still between its Keycloak add and its commit, so one that relied
    /// on this membership has committed its relationship when the re-check runs, and a join that starts meanwhile waits
    /// until the revoke is done and adds the membership itself. When the re-check fails, or the lock is not had within
    /// <see cref="JoinLock.UndoWait"/>, nothing is taken back and the failure is logged: the user then stays a member
    /// without a relationship, so the Vendor policy (which checks the organization and the vendor row, not the
    /// relationship) opens the vendor home on this host, where the company shows no status with the tenant and the
    /// tenant's staff do not see it. Joining again creates the relationship.
    /// </summary>
    private async Task UndoAsync(TenantContext tenant, Guid companyId, string userId)
    {
        await using var db = await contexts.CreateDbContextAsync(CancellationToken.None);
        IDbContextTransaction transaction;
        bool related;
        try
        {
            transaction = await db.Database.BeginTransactionAsync(CancellationToken.None);
            await JoinLock.ExclusiveAsync(db, tenant.TenantId, companyId, CancellationToken.None);
            related = await db.Relationships.AsNoTracking().AnyAsync(r => r.CompanyId == companyId, CancellationToken.None);
        }
        catch (Exception ex) when (ex is DbException or TimeoutException or InvalidOperationException)
        {
            RecheckFailed(logger, tenant.Slug, userId, ex.GetType().Name);
            return;
        }

        // Nothing to commit: disposing the transaction rolls it back, which releases the lock after the revoke.
        await using (transaction)
        {
            if (!related)
            {
                await accounts.RevokeAsync(
                    new VendorAccessGrant(userId, tenant.KeycloakOrgAlias, RoleAdded: false, OrganizationAdded: true), CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// W-21 pentest P-1: the company already works with this tenant, so joining never adds the user to the organization.
    /// A user still in it changes nothing (its next sign-in carries the organization); a user no longer in it was removed
    /// by the tenant, and only the tenant restores access (a staff action), so the join is refused and the refusal audited
    /// as <c>vendor.membership_restore_refused</c>. A blocked relationship (F-14) is related too, so a block holds here as
    /// long as it also takes the user out of the organization.
    /// </summary>
    private async Task<Result<VendorJoined>> RejoinAsync(TenantContext tenant, Guid companyId, string userId, CancellationToken cancellationToken)
    {
        VendorAccountState state;
        try
        {
            state = await accounts.DescribeAsync(userId, cancellationToken);
        }
        catch (IdentityProviderException ex)
        {
            KeycloakFailed(logger, tenant.Slug, userId, ex.InnerException?.GetType().Name ?? ex.GetType().Name);
            return Failed();
        }

        if (state.OrganizationAliases.Contains(tenant.KeycloakOrgAlias, StringComparer.Ordinal))
        {
            return Result.Success(new VendorJoined(RelationshipCreated: false, OrganizationAdded: false));
        }

        // W-33: the claimant of an upheld dispute gets every related organization from the uphold itself; when the add to
        // this tenant's organization failed (or was never recorded) and waits for the platform admin's retry, say so rather
        // than "your access was removed". Any other failed step of the dispute does not matter here. Still no membership
        // from here (P-1).
        if (await AwaitingOrganizationAsync(tenant.KeycloakOrgAlias, cancellationToken))
        {
            return Result.Failure<VendorJoined>(Error.Refused(
                VendorErrors.MembershipPendingRetry,
                "WaslaBid is still giving your account access to this organization after moving your company to you. Try again later."));
        }

        try
        {
            await audit.WriteAsync(
                new AuditEntry(userId, "vendor.membership_restore_refused", "vendor_company", companyId.ToString()), cancellationToken);
        }
        catch (Exception ex) when (ex is DbException or DbUpdateException or TimeoutException or InvalidOperationException)
        {
            // The refusal stands without its audit entry; the log records it.
            RefusalNotAudited(logger, tenant.Slug, userId, ex.GetType().Name);
        }

        return Result.Failure<VendorJoined>(Error.Refused(
            VendorErrors.MembershipRemoved, "Your access to this organization was removed. Only the organization can restore it."));
    }

    private async Task<bool> AwaitingOrganizationAsync(string organizationAlias, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.Database.SqlQuery<bool>(
            $"select vendor.claimant_awaiting_organization({organizationAlias}) as \"Value\"").SingleAsync(cancellationToken);
    }

    private static Result<VendorJoined> Failed() =>
        Result.Failure<VendorJoined>(Error.Refused(VendorErrors.JoinFailed, "Joining this organization could not be completed. Try again in a moment."));

    [LoggerMessage(Level = LogLevel.Warning, Message = "Keycloak did not add the vendor to tenant {Tenant}'s organization, user {UserId} ({ErrorType}).")]
    private static partial void KeycloakFailed(ILogger logger, string tenant, string userId, string errorType);

    [LoggerMessage(Level = LogLevel.Error, Message = "The vendor's relationship with tenant {Tenant} was not saved, user {UserId} ({ErrorType}).")]
    private static partial void SaveFailed(ILogger logger, string tenant, string userId, string errorType);

    [LoggerMessage(Level = LogLevel.Error, Message = "The refused membership restore of user {UserId} on tenant {Tenant} was not audited ({ErrorType}).")]
    private static partial void RefusalNotAudited(ILogger logger, string tenant, string userId, string errorType);

    [LoggerMessage(Level = LogLevel.Error, Message = "After a failed join of tenant {Tenant} by user {UserId} the relationship was not re-checked, so the organization membership was left in place ({ErrorType}).")]
    private static partial void RecheckFailed(ILogger logger, string tenant, string userId, string errorType);
}
