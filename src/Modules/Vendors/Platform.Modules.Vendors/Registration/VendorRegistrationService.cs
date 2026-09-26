using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Platform.Modules.Audit.Contracts;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Operations.Contracts;
using Platform.Modules.Vendors.Access;
using Platform.Modules.Vendors.Contracts;
using Platform.Modules.Vendors.Persistence;
using Platform.Shared.Results;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Vendors.Registration;

/// <summary>
/// Vendor self-registration through a tenant host (F-11, V-3 to V-7, V-14). The order keeps every refusal before any
/// change: the input, a user who already has a company, a staff member of the host tenant, a user limited for testing CR
/// numbers, a CR number already on the platform (V-6, audited in the platform audit under its SHA-256), and the Keycloak
/// account: a member of any organization is refused as staff, unless it holds the realm role <c>vendor</c> and belongs
/// only to the host tenant's organization, which is a registration that stopped half way and may finish. Then Keycloak
/// (the role, then the organization), then the company, its first vendor admin and the pending relationship in one
/// database call. After any failure, what this attempt added in Keycloak is taken back only when the user still has no
/// company, so a parallel registration of the same user that won keeps its access. The tenant is always the request's;
/// the user is the acting user the host set from the principal (and the database takes it from the session, not from
/// this service); the email comes from the caller's principal, never from the form.
/// </summary>
internal sealed partial class VendorRegistrationService(
    IDbContextFactory<VendorsDbContext> contexts,
    VendorUsers users,
    IMemberDirectory members,
    IVendorAccounts accounts,
    ITenantAccessor tenants,
    IActingUserAccessor actingUser,
    IAuditWriter audit,
    IPlatformAudit platformAudit,
    DuplicateCrThrottle duplicates,
    ILogger<VendorRegistrationService> logger) : IVendorRegistration
{
    /// <summary>V-6: the same words whatever the company, so the form tells nothing about it.</summary>
    internal const string DuplicateMessage = "This company already has an account on WaslaBid. Ask its administrator to add you.";

    private const string CrConstraint = "ux_companies_cr_number";

    public IReadOnlyList<Error> Validate(VendorRegistration registration) => VendorInput.Validate(registration);

    public async Task<VendorRegistrationCheck> CheckAsync(CancellationToken cancellationToken = default)
    {
        var (tenant, userId) = Caller();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        if (await VendorUsers.CompanyOfAsync(db, userId, cancellationToken) is not null)
        {
            return VendorRegistrationCheck.AlreadyRegistered;
        }

        if ((await members.GetRolesAsync(userId, cancellationToken)).Count > 0)
        {
            return VendorRegistrationCheck.StaffAccount;
        }

        try
        {
            return MayRegister(await accounts.DescribeAsync(userId, cancellationToken), tenant)
                ? VendorRegistrationCheck.Open
                : VendorRegistrationCheck.StaffAccount;
        }
        catch (Exception ex) when (ex is IdentityProviderException or InvalidOperationException)
        {
            // The page shows the form; the registration asks Keycloak again and refuses if it still cannot.
            KeycloakFailed(logger, tenant.Slug, userId, ex.InnerException?.GetType().Name ?? ex.GetType().Name);
            return VendorRegistrationCheck.Unknown;
        }
    }

    public async Task<Result<Guid>> RegisterCompanyAsync(VendorRegistration registration, string email, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        var (tenant, userId) = Caller();

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

        // A limited user gets the duplicate answer whatever the number, before the number is looked up.
        if (duplicates.IsLimited(userId))
        {
            return DuplicateAnswer();
        }

        if (await db.Database.SqlQuery<bool>($"select vendor.cr_exists({input.CrNumber}) as \"Value\"").SingleAsync(cancellationToken))
        {
            return await DuplicateAsync(tenant, userId, input.CrNumber, cancellationToken);
        }

        var grant = new VendorAccessGrant(userId, tenant.KeycloakOrgAlias, RoleAdded: false, OrganizationAdded: false);
        try
        {
            if (!MayRegister(await accounts.DescribeAsync(userId, cancellationToken), tenant))
            {
                return StaffAccount();
            }

            grant = grant with { RoleAdded = await accounts.GrantRoleAsync(userId, cancellationToken) };
            grant = grant with { OrganizationAdded = await accounts.AddToOrganizationAsync(userId, tenant.KeycloakOrgAlias, cancellationToken) };
        }
        catch (IdentityProviderException ex)
        {
            KeycloakFailed(logger, tenant.Slug, userId, ex.InnerException?.GetType().Name ?? ex.GetType().Name);
            return await UndoAsync(grant, Failed());
        }

        Guid companyId;
        try
        {
            companyId = await db.Database.SqlQuery<Guid>($"""
                select vendor.register_company({input.CrNumber}, {input.NameAr}, {input.NameEn}, {input.VatNumber}, {input.Address},
                                               {input.ContactName}, {input.ContactPhone}, {input.ContactEmail},
                                               {input.PrivacyNoticeVersion}, {input.PrivacyNoticeCulture}) as "Value"
                """).SingleAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == CrConstraint)
        {
            // Another registration took the CR number since the check above; this one gave Keycloak access for nothing.
            var duplicate = await DuplicateAsync(tenant, userId, input.CrNumber, cancellationToken);
            return await UndoAsync(grant, duplicate);
        }
        catch (Exception ex) when (ex is DbException or TimeoutException or OperationCanceledException or InvalidOperationException)
        {
            // Includes the same user registering a company at the same moment in another request (a unique violation on
            // the user): the re-check in UndoAsync then finds that company, and its access is left alone.
            SaveFailed(logger, tenant.Slug, userId, ex.GetType().Name);
            var outcome = await UndoAsync(grant, Failed());
            if (ex is OperationCanceledException)
            {
                throw;
            }

            return outcome;
        }

        users.Forget(userId);
        await audit.WriteAsync(
            new AuditEntry(userId, "vendor.registered", "vendor_company", companyId.ToString(), new Dictionary<string, string?>
            {
                ["cr_number"] = input.CrNumber,
                ["email"] = email,
                ["privacy_notice_version"] = input.PrivacyNoticeVersion,
                ["privacy_notice_culture"] = input.PrivacyNoticeCulture,
                ["role_added"] = grant.RoleAdded ? "true" : "false",
                ["organization_added"] = grant.OrganizationAdded ? "true" : "false",
            }),
            cancellationToken);
        return Result.Success(companyId);
    }

    private (TenantContext Tenant, string UserId) Caller() =>
        (tenants.Current ?? throw new InvalidOperationException("A vendor company is registered through a tenant host; this request has none."),
         actingUser.UserId ?? throw new InvalidOperationException("A vendor company is registered by a signed-in user; this request has none."));

    /// <summary>
    /// No organization at all, or a registration that stopped half way on this host: the realm role and the host tenant's
    /// organization alone (the company row, checked before, is missing). Anything else is staff, or a vendor of another
    /// tenant who should join rather than register.
    /// </summary>
    private static bool MayRegister(VendorAccountState account, TenantContext tenant) =>
        account.OrganizationAliases.Count == 0
        || (account.HoldsVendorRole && account.OrganizationAliases.All(a => string.Equals(a, tenant.KeycloakOrgAlias, StringComparison.Ordinal)));

    /// <summary>
    /// Takes back what this attempt added in Keycloak, but only when the user still has no company: a parallel registration
    /// of the same user that succeeded owns the role and membership, and this attempt then answers as already registered.
    /// When the re-check itself fails nothing is taken back; the account is then a half-finished registration, which the
    /// Vendor policy keeps closed (no company row) and a new attempt may finish.
    /// </summary>
    private async Task<Result<Guid>> UndoAsync(VendorAccessGrant grant, Result<Guid> outcome)
    {
        if (!grant.RoleAdded && !grant.OrganizationAdded)
        {
            return outcome;
        }

        Guid? company;
        try
        {
            await using var db = await contexts.CreateDbContextAsync(CancellationToken.None);
            company = await VendorUsers.CompanyOfAsync(db, grant.UserId, CancellationToken.None);
        }
        catch (Exception ex) when (ex is DbException or TimeoutException or InvalidOperationException)
        {
            RecheckFailed(logger, grant.UserId, ex.GetType().Name);
            return outcome;
        }

        if (company is not null)
        {
            return AlreadyRegistered();
        }

        await accounts.RevokeAsync(grant, CancellationToken.None);
        return outcome;
    }

    /// <summary>
    /// V-6: refused with the neutral message and audited in the platform audit under the SHA-256 of the CR number, never
    /// the number itself and never in a tenant's log (the host tenant need not learn which companies its visitors tried).
    /// Counts towards the user's limit (<see cref="DuplicateCrThrottle"/>).
    /// </summary>
    private async Task<Result<Guid>> DuplicateAsync(TenantContext tenant, string userId, string crNumber, CancellationToken cancellationToken)
    {
        duplicates.Record(userId);
        await platformAudit.WriteAsync(
            new PlatformAuditEntry(userId, "vendor.duplicate_cr_refused", "cr_number_sha256", Sha256(crNumber), new Dictionary<string, string?>
            {
                ["tenant"] = tenant.Slug,
            }),
            cancellationToken);
        return DuplicateAnswer();
    }

    private static string Sha256(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static Result<Guid> DuplicateAnswer() => Result.Failure<Guid>(Error.Conflict(VendorErrors.DuplicateCr, DuplicateMessage));

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

    [LoggerMessage(Level = LogLevel.Error, Message = "After a failed vendor registration of user {UserId} the company was not re-checked, so Keycloak access was left in place ({ErrorType}).")]
    private static partial void RecheckFailed(ILogger logger, string userId, string errorType);
}
