using Platform.Shared.Results;

namespace Platform.Modules.Vendors.Contracts;

/// <summary>What a vendor company consents to hand to a recipient (V-12, ADR-0010 point 1).</summary>
public enum ConsentScope
{
    /// <summary>Its award records (F-65).</summary>
    AwardRecords,

    /// <summary>Its purchase order records.</summary>
    PoRecords,

    /// <summary>Its profile and documents.</summary>
    ProfileDocuments,
}

/// <summary>Where a grant stands today (Riyadh time).</summary>
public enum ConsentStatus
{
    /// <summary>In its period and not revoked: a check relies on it.</summary>
    Active,

    /// <summary>Its period starts later.</summary>
    NotYetValid,

    /// <summary>Its period ended.</summary>
    Expired,

    /// <summary>Revoked by a later row; the grant row itself is unchanged.</summary>
    Revoked,
}

/// <summary>A named recipient on the platform's list (V-13), named in both languages.</summary>
public sealed record ConsentRecipient(Guid Id, string NameAr, string NameEn);

/// <summary>
/// A grant of the current vendor company as its ledger shows it: recipient, scope, period, who granted it and when, the
/// revocation if there is one, and its status today.
/// </summary>
public sealed record ConsentGrant(
    Guid Id,
    ConsentRecipient Recipient,
    ConsentScope Scope,
    DateOnly ValidFrom,
    DateOnly ValidTo,
    string GrantedBy,
    DateTimeOffset GrantedAt,
    string? RevokedBy,
    DateTimeOffset? RevokedAt,
    ConsentStatus Status);

/// <summary>The answer of a consent check: allowed, and the grant it relied on; or refused, with no grant.</summary>
public sealed record ConsentCheckResult(bool Allowed, Guid? GrantId);

/// <summary>
/// The vendor consent ledger (F-64, V-12, V-13, ADR-0010). A grant is a row and a revocation a new row; nothing is updated
/// or deleted. Granting, revoking and listing work only in a vendor context, as a vendor admin of that company, with the
/// acting user as the actor; a tenant can never grant for a vendor. <see cref="CheckAsync"/> is the only path any export of
/// vendor data to a third party may use. Every grant, revocation and check is audited in the platform audit (the consent
/// concerns the platform-level company and recipient, not a tenant; each entry names the host tenant when there is one).
/// </summary>
public interface IConsentLedger
{
    /// <summary>The recipients a vendor may grant consent to (maintained by migration; none in production yet).</summary>
    Task<IReadOnlyList<ConsentRecipient>> ListRecipientsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Grants consent to <paramref name="recipientId"/> for <paramref name="scope"/> from <paramref name="validFrom"/> through
    /// <paramref name="validTo"/>, audited as <c>vendor.consent_granted</c>; returns the grant id. The period starts today
    /// (Riyadh) or later and ends on or after its first day (<see cref="ConsentErrors.InvalidPeriod"/>); an unknown recipient
    /// is <see cref="ConsentErrors.UnknownRecipient"/>. Grants are limited per company (W-35,
    /// <see cref="ConsentErrors.RateLimited"/>), counted only for a grant that passed the other checks and refused before
    /// any row or audit entry. Throws <see cref="InvalidOperationException"/> without a vendor context, or when
    /// <paramref name="actorId"/> is not the acting user.
    /// </summary>
    Task<Result<Guid>> GrantAsync(
        Guid recipientId, ConsentScope scope, DateOnly validFrom, DateOnly validTo, string actorId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes a grant of the company with a new row, audited as <c>vendor.consent_revoked</c>; returns the revocation id.
    /// A grant of another company, or no grant, is <see cref="ConsentErrors.GrantNotFound"/>; a revoked one
    /// <see cref="ConsentErrors.AlreadyRevoked"/>. Never rate limited: consent can be withdrawn at any time (PDPL, W-35).
    /// Throws as <see cref="GrantAsync"/> does.
    /// </summary>
    Task<Result<Guid>> RevokeAsync(Guid grantId, string actorId, CancellationToken cancellationToken = default);

    /// <summary>Every grant of the company, newest first, with its revocation and status today.</summary>
    Task<IReadOnlyList<ConsentGrant>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the company has a grant to the recipient for the scope in force today in Riyadh, at the moment the export
    /// runs (ADR-0010 point 2): inside its period, first and last day included, and never revoked; and which one (the newest
    /// when several are). The date is the database's, never the caller's, so an export cannot rely on a grant that starts
    /// later or has ended. Audited as <c>vendor.consent_check</c> with the result, the grant relied on and the host tenant,
    /// under the acting user; an application-role session (web, console) must have one (W-41), and only the worker, which has none, audits without it (otherwise <see cref="InvalidOperationException"/>). Asked by an export without a context, by tenant staff only about a company
    /// their tenant works with, or by the company itself; another company's vendor context, or a tenant with no relationship
    /// with the company, is refused (<see cref="InvalidOperationException"/>).
    /// </summary>
    Task<ConsentCheckResult> CheckAsync(
        Guid companyId, Guid recipientId, ConsentScope scope, CancellationToken cancellationToken = default);
}

/// <summary>Stable error codes of the consent ledger; pages map them to localized text.</summary>
public static class ConsentErrors
{
    public const string UnknownRecipient = "consent.unknown_recipient";
    public const string InvalidPeriod = "consent.invalid_period";
    public const string GrantNotFound = "consent.grant_not_found";
    public const string AlreadyRevoked = "consent.already_revoked";
    public const string NotVendorAdmin = "consent.not_vendor_admin";

    /// <summary>
    /// W-35: the company made too many grants in the last hour (<c>Vendors:ConsentGrantsPerCompanyPerHour</c>); nothing was
    /// written or audited. Revocations are never refused for this.
    /// </summary>
    public const string RateLimited = "consent.rate_limited";
}
