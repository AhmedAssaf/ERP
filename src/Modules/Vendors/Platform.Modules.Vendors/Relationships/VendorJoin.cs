using System.Data.Common;
using Microsoft.EntityFrameworkCore;
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
/// A vendor of another tenant joins the host tenant (spec section 3, ADR-0008). Keycloak first: membership of the tenant's
/// organization, which the Vendor policy needs there; then, in one database transaction, <c>vendor.join_tenant()</c> (a
/// pending relationship for the session's tenant and vendor company, only for a user of that company), which answers
/// whether this call created it. The audit entry is written before that transaction commits, but on the audit writer's
/// own connection, so it is not part of the transaction: when the audit fails the relationship rolls back; a commit that
/// fails after it leaves an entry for a join that did not happen, and joining again writes a second one. Only the call
/// that created the relationship writes <c>vendor.joined</c>, so two joins at the same moment write one; a call that added
/// the user to the organization while a parallel join created the relationship writes <c>vendor.membership_restored</c>.
/// When the database step fails, the membership this call added is taken back unless a
/// relationship with the tenant exists by then (a parallel join of the same vendor won).
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

        bool organizationAdded;
        try
        {
            organizationAdded = await accounts.AddToOrganizationAsync(userId, tenant.KeycloakOrgAlias, cancellationToken);
        }
        catch (IdentityProviderException ex)
        {
            KeycloakFailed(logger, tenant.Slug, userId, ex.InnerException?.GetType().Name ?? ex.GetType().Name);
            return Failed();
        }

        try
        {
            await using var db = await contexts.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
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
        catch (Exception ex) when (ex is DbException or DbUpdateException or TimeoutException or OperationCanceledException or InvalidOperationException)
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
    /// now. When that re-check fails nothing is taken back and the failure is logged: the user then stays a member without a
    /// relationship, so the Vendor policy (which checks the organization and the vendor row, not the relationship) opens the
    /// vendor home on this host, where the company shows no status with the tenant and the tenant's staff do not see it.
    /// Joining again creates the relationship.
    /// </summary>
    private async Task UndoAsync(TenantContext tenant, Guid companyId, string userId)
    {
        bool related;
        try
        {
            await using var db = await contexts.CreateDbContextAsync(CancellationToken.None);
            related = await db.Relationships.AsNoTracking().AnyAsync(r => r.CompanyId == companyId, CancellationToken.None);
        }
        catch (Exception ex) when (ex is DbException or TimeoutException or InvalidOperationException)
        {
            RecheckFailed(logger, tenant.Slug, userId, ex.GetType().Name);
            return;
        }

        if (!related)
        {
            await accounts.RevokeAsync(
                new VendorAccessGrant(userId, tenant.KeycloakOrgAlias, RoleAdded: false, OrganizationAdded: true), CancellationToken.None);
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
