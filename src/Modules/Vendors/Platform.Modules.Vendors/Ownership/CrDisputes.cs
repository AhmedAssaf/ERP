using Microsoft.EntityFrameworkCore;
using Npgsql;
using Platform.Modules.Operations.Contracts;
using Platform.Modules.Vendors.Contracts;
using Platform.Modules.Vendors.Persistence;
using Platform.Modules.Vendors.Registration;
using Platform.Shared.Results;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Vendors.Ownership;

/// <summary>
/// The claimant's side of a CR dispute (W-33, <see cref="ICrDisputes"/>): a signed-in person on a tenant host, outside
/// any vendor session, claims the company registered under a CR number. The claimant is the acting user; the email and
/// name come from the caller's verified token, never from the form. A CR number without a company counts toward the
/// duplicate-CR limit of the registration (V-6), so the dispute form is no faster way to probe CR numbers.
/// </summary>
internal sealed class CrDisputes(
    IDbContextFactory<VendorsDbContext> contexts,
    ITenantAccessor tenants,
    IVendorAccessor vendors,
    IActingUserAccessor actingUser,
    IPlatformAudit audit,
    DuplicateCrThrottle duplicates) : ICrDisputes
{
    public IReadOnlyList<Error> Validate(CrDisputeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new List<Error>();
        if (!VendorInput.IsCrNumber(VendorInput.Digits(request.CrNumber)))
        {
            errors.Add(Error.Validation(CrDisputeErrors.InvalidCrNumber, "Enter the 10-digit commercial registration (CR) number, using digits only."));
        }

        if (!VendorInput.IsFreeText(request.Statement, OwnershipStore.MaxStatementLength))
        {
            errors.Add(Error.Validation(
                CrDisputeErrors.StatementRequired, $"Explain in up to {OwnershipStore.MaxStatementLength} characters why the company is yours."));
        }

        if (!string.Equals(request.AcceptedPrivacyNotice, VendorPrivacyNotice.CurrentVersion, StringComparison.Ordinal))
        {
            errors.Add(Error.Validation(CrDisputeErrors.PrivacyNoticeRequired, "Accept the privacy notice to send the request."));
        }

        return errors;
    }

    public async Task<Result<Guid>> RaiseAsync(CrDisputeRequest request, string email, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var (tenant, userId) = Caller();
        if (Validate(request) is [var first, ..])
        {
            return Result.Failure<Guid>(first);
        }

        if (await duplicates.IsLimitedAsync(userId, cancellationToken))
        {
            return Result.Failure<Guid>(Error.Refused(CrDisputeErrors.Limited, "Too many commercial registration numbers were tried. Try again in an hour."));
        }

        var crNumber = VendorInput.Digits(request.CrNumber);
        var statement = VendorInput.NormalizeFreeText(request.Statement);
        var culture = VendorPrivacyNotice.Cultures.Contains(request.PrivacyNoticeCulture, StringComparer.Ordinal)
            ? request.PrivacyNoticeCulture!
            : VendorPrivacyNotice.English;
        var claimantName = name.Trim().Length > 200 ? name.Trim()[..200] : name.Trim();

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        Guid? disputeId;
        try
        {
            disputeId = await db.Database.SqlQuery<Guid?>($"""
                select vendor.raise_cr_dispute({crNumber}, {email.Trim()}, {claimantName}, {statement},
                                               {VendorPrivacyNotice.CurrentVersion}, {culture}) as "Value"
                """).SingleAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == "ux_cr_disputes_open")
        {
            return Result.Failure<Guid>(Error.Conflict(CrDisputeErrors.AlreadyOpen, "You already have an open request for this company. WaslaBid will contact you."));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.CheckViolation)
        {
            return Result.Failure<Guid>(ex.ConstraintName switch
            {
                "ck_cr_disputes_claimant_not_vendor" => Error.Refused(
                    CrDisputeErrors.AlreadyVendor, "This account already belongs to a vendor company. Sign in with a separate account."),
                "ck_cr_disputes_claimant_not_staff" => Error.Refused(
                    CrDisputeErrors.StaffAccount, "This account belongs to a staff member. Use a separate account for your company."),
                "ck_cr_disputes_open_limit" => Error.Refused(
                    CrDisputeErrors.TooManyOpen, "You have three open requests. Wait for WaslaBid to close one."),
                _ => throw new InvalidOperationException($"The dispute was refused under an unexpected rule ({ex.ConstraintName}).", ex),
            });
        }

        if (disputeId is not { } id)
        {
            await duplicates.RecordAsync(userId, cancellationToken);
            return Result.Failure<Guid>(Error.NotFound(
                CrDisputeErrors.NoCompany, "No company on WaslaBid has this commercial registration number. Register your company instead."));
        }

        await audit.WriteAsync(
            new PlatformAuditEntry(userId, "vendor.dispute_raised", "cr_dispute", id.ToString(), new Dictionary<string, string?>
            {
                ["tenant"] = tenant.Slug,
                ["email"] = email.Trim(),
                ["privacy_notice_version"] = VendorPrivacyNotice.CurrentVersion,
                ["privacy_notice_culture"] = culture,
            }),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Result.Success(id);
    }

    public async Task<IReadOnlyList<OwnCrDispute>> ListOwnAsync(CancellationToken cancellationToken = default)
    {
        Caller();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var rows = await db.Database.SqlQuery<OwnRow>($"select id, cr_number, raised_at, status from vendor.my_cr_disputes()")
            .ToListAsync(cancellationToken);
        return [.. rows.Select(r => new OwnCrDispute(r.Id, r.CrNumber, r.RaisedAt, r.Status))];
    }

    private (TenantContext Tenant, string UserId) Caller()
    {
        if (vendors.Current is not null)
        {
            throw new InvalidOperationException("A dispute is raised outside any vendor session.");
        }

        return (tenants.Current ?? throw new InvalidOperationException("A dispute is raised on a tenant host; this request has none."),
                actingUser.UserId ?? throw new InvalidOperationException("A dispute is raised by a signed-in person; this request has none."));
    }

    private sealed class OwnRow
    {
        public Guid Id { get; set; }

        public string CrNumber { get; set; } = string.Empty;

        public DateTimeOffset RaisedAt { get; set; }

        public string Status { get; set; } = string.Empty;
    }
}
