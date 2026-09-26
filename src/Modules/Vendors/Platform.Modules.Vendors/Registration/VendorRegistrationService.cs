using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Platform.Modules.Audit.Contracts;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Vendors.Access;
using Platform.Modules.Vendors.Contracts;
using Platform.Modules.Vendors.Persistence;
using Platform.Shared.Results;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Vendors.Registration;

/// <summary>
/// Vendor self-registration through a tenant host (F-11, V-3 to V-7, V-14). The order keeps every refusal before any
/// change: the input, a user who already has a company, a staff member of the host tenant, a CR number already on the
/// platform (V-6, audited), and membership of any organization (staff of another tenant, or a vendor who should join
/// instead). Then Keycloak first (realm role <c>vendor</c>, the host tenant's organization), then the company, its first
/// vendor admin and the pending relationship in one database call, so a failed database call takes back exactly what
/// Keycloak was given, and a failed Keycloak call leaves no row. The tenant is always the request's; the user id and
/// email come from the caller's principal, never from the form.
/// </summary>
internal sealed partial class VendorRegistrationService(
    IDbContextFactory<VendorsDbContext> contexts,
    VendorUsers users,
    IMemberDirectory members,
    IVendorAccounts accounts,
    ITenantAccessor tenants,
    IAuditWriter audit,
    ILogger<VendorRegistrationService> logger) : IVendorRegistration
{
    /// <summary>V-6: the same words whatever the company, so the form tells nothing about it.</summary>
    internal const string DuplicateMessage = "This company already has an account on WaslaBid. Ask its administrator to add you.";

    private const string CrConstraint = "ux_companies_cr_number";

    public IReadOnlyList<Error> Validate(VendorRegistration registration) => VendorInput.Validate(registration);

    public async Task<Result<Guid>> RegisterCompanyAsync(
        VendorRegistration registration, string userId, string email, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        var tenant = tenants.Current ?? throw new InvalidOperationException("A vendor company is registered through a tenant host; this request has none.");

        if (VendorInput.Validate(registration) is [var first, ..])
        {
            return Result.Failure<Guid>(first);
        }

        var input = VendorInput.Normalize(registration);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        if (await VendorUsers.CompanyOfAsync(db, userId, cancellationToken) is not null)
        {
            return AlreadyRegistered();
        }

        if ((await members.GetRolesAsync(userId, cancellationToken)).Count > 0)
        {
            return StaffAccount();
        }

        if (await db.Database.SqlQuery<bool>($"select vendor.cr_exists({input.CrNumber}) as \"Value\"").SingleAsync(cancellationToken))
        {
            return await DuplicateAsync(userId, input.CrNumber, cancellationToken);
        }

        VendorAccessGrant grant;
        try
        {
            if (await accounts.BelongsToAnyOrganizationAsync(userId, cancellationToken))
            {
                return StaffAccount();
            }

            grant = await accounts.GrantAsync(userId, tenant.KeycloakOrgAlias, cancellationToken);
        }
        catch (IdentityProviderException ex)
        {
            KeycloakFailed(logger, tenant.Slug, userId, ex.InnerException?.GetType().Name ?? ex.GetType().Name);
            return Failed();
        }

        Guid companyId;
        try
        {
            companyId = await db.Database.SqlQuery<Guid>($"""
                select vendor.register_company({input.CrNumber}, {input.NameAr}, {input.NameEn}, {input.VatNumber}, {input.Address},
                                               {input.ContactName}, {input.ContactPhone}, {input.ContactEmail},
                                               {userId}, {input.PrivacyNoticeVersion}) as "Value"
                """).SingleAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == CrConstraint)
        {
            // Another registration took the CR number since the check above; this one gave Keycloak access for nothing.
            await accounts.RevokeAsync(grant, CancellationToken.None);
            return await DuplicateAsync(userId, input.CrNumber, cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // The same user registered a company at the same moment in another request; that registration owns the
            // Keycloak role and membership, so nothing is taken back.
            return AlreadyRegistered();
        }
        catch (Exception ex) when (ex is DbException or TimeoutException or OperationCanceledException or InvalidOperationException)
        {
            SaveFailed(logger, tenant.Slug, userId, ex.GetType().Name);
            await accounts.RevokeAsync(grant, CancellationToken.None);
            if (ex is OperationCanceledException)
            {
                throw;
            }

            return Failed();
        }

        users.Forget(userId);
        await audit.WriteAsync(
            new AuditEntry(userId, "vendor.registered", "vendor_company", companyId.ToString(), new Dictionary<string, string?>
            {
                ["cr_number"] = input.CrNumber,
                ["email"] = email,
                ["privacy_notice_version"] = input.PrivacyNoticeVersion,
                ["organization_added"] = grant.OrganizationAdded ? "true" : "false",
            }),
            cancellationToken);
        return Result.Success(companyId);
    }

    /// <summary>V-6: refused with the neutral message and audited in the host tenant's log, naming the CR number only.</summary>
    private async Task<Result<Guid>> DuplicateAsync(string userId, string crNumber, CancellationToken cancellationToken)
    {
        await audit.WriteAsync(new AuditEntry(userId, "vendor.duplicate_cr_refused", "cr_number", crNumber), cancellationToken);
        return Result.Failure<Guid>(Error.Conflict(VendorErrors.DuplicateCr, DuplicateMessage));
    }

    private static Result<Guid> AlreadyRegistered() =>
        Result.Failure<Guid>(Error.Conflict(VendorErrors.AlreadyRegistered, "This account already belongs to a vendor company."));

    private static Result<Guid> StaffAccount() =>
        Result.Failure<Guid>(Error.Refused(
            VendorErrors.StaffAccount, "This account belongs to a staff member. Register your company with a separate account."));

    private static Result<Guid> Failed() =>
        Result.Failure<Guid>(Error.Refused(VendorErrors.RegistrationFailed, "The registration could not be completed. Try again in a moment."));

    [LoggerMessage(Level = LogLevel.Warning, Message = "Keycloak did not complete the vendor registration for tenant {Tenant}, user {UserId} ({ErrorType}).")]
    private static partial void KeycloakFailed(ILogger logger, string tenant, string userId, string errorType);

    [LoggerMessage(Level = LogLevel.Error, Message = "The vendor company was not saved for tenant {Tenant}, user {UserId} ({ErrorType}).")]
    private static partial void SaveFailed(ILogger logger, string tenant, string userId, string errorType);
}
