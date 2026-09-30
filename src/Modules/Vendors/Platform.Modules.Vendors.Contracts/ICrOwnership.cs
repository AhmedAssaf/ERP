using Platform.Shared.Results;

namespace Platform.Modules.Vendors.Contracts;

/// <summary>
/// How the platform checks that the person who registered a CR number owns the company (W-33): one platform-level setting,
/// changed only in the platform console (<see cref="ICrOwnershipAdministration"/>). The approving officer confirms in
/// both; Wathq only shows the officer the CR's owners and managers first.
/// </summary>
public enum CrOwnershipMethod
{
    /// <summary>The officer checks the company's CR certificate (F-12) against the registering person. The default.</summary>
    Manual,

    /// <summary>The CR's owners and managers are read from Wathq and shown to the officer beside the registering person.</summary>
    Wathq,
}

/// <summary>How a company's ownership was verified.</summary>
public enum OwnershipVerificationMethod
{
    /// <summary>An officer checked the CR certificate.</summary>
    Manual,

    /// <summary>An officer checked the CR's owners and managers from Wathq.</summary>
    Wathq,

    /// <summary>A platform admin upheld a dispute and moved the company to the claimant.</summary>
    Dispute,
}

/// <summary>What the lookup of <see cref="CrOwnershipLookup"/> found, or why the officer checks by hand.</summary>
public enum CrLookupOutcome
{
    /// <summary>The platform uses the manual check; nothing was looked up.</summary>
    Manual,

    /// <summary>Wathq answered with the CR's owners and managers.</summary>
    Found,

    /// <summary>Wathq is the method, but its address or API key is not configured: the manual check applies.</summary>
    NotConfigured,

    /// <summary>Wathq did not answer, or answered with an error: the manual check applies.</summary>
    Unavailable,

    /// <summary>Wathq has no record for the CR number: the manual check applies.</summary>
    NotFound,
}

/// <summary>An owner, partner or manager as the commercial registry names them: never their identity number.</summary>
public sealed record CrParty(string Name, string? Type, IReadOnlyList<string> Positions);

/// <summary>The CR's owners and managers when <see cref="Outcome"/> is <see cref="CrLookupOutcome.Found"/>; empty otherwise.</summary>
public sealed record CrOwnershipLookup(CrLookupOutcome Outcome, IReadOnlyList<CrParty> Owners, IReadOnlyList<CrParty> Managers)
{
    public static CrOwnershipLookup Without(CrLookupOutcome outcome) => new(outcome, [], []);
}

/// <summary>
/// The person who registered the company, as the identity provider knows them; null parts are unknown. The name is
/// self-declared at sign-up, never checked by anyone (ADR-0013 residual risk); <see cref="EmailVerified"/> says whether the
/// identity provider verified the email.
/// </summary>
public sealed record VendorRegistrant(string UserId, string? Name, string? Email, bool EmailVerified = false);

/// <summary>
/// What an officer needs before a company's first approval (W-33). <see cref="Verified"/>: ownership was verified already,
/// here or at another tenant (which one is never told), and the approval needs no check; <see cref="Registrant"/> and
/// <see cref="Lookup"/> are then null. Otherwise the registering person and the lookup of the platform's method; no
/// lookup (null) when the company is held or has no current CR certificate, since the check could not pass anyway and a
/// Wathq call is paid. <see cref="Disputed"/>: the company is held by a dispute (ADR-0013 decision 1): a verified company
/// only by one a platform admin accepted for review, a company not verified yet by any pending claim.
/// </summary>
public sealed record OwnershipCheck(
    Guid CompanyId,
    string CrNumber,
    bool Verified,
    OwnershipVerificationMethod? VerifiedMethod,
    bool Disputed,
    bool HasCertificate,
    VendorRegistrant? Registrant,
    CrOwnershipLookup? Lookup);

/// <summary>
/// The officer's confirmation that the current CR certificate names the registering person, or that an authorisation backs
/// them, with a note of 1 to 1,000 characters. <see cref="BasedOnWathq"/> when the officer compared the Wathq answer
/// (<see cref="LookupOutcome"/> is then <see cref="CrLookupOutcome.Found"/>); the outcome the dialog showed is audited.
/// </summary>
public sealed record OwnershipConfirmation(string? Note, bool BasedOnWathq, CrLookupOutcome LookupOutcome);

/// <summary>The platform's check method as the console shows it.</summary>
public sealed record CrOwnershipSettings(CrOwnershipMethod Method, bool WathqConfigured, string? ChangedBy, DateTimeOffset ChangedAt);

/// <summary>Where a dispute stands (ADR-0013 decision 1).</summary>
public enum CrDisputeStatus
{
    /// <summary>Raised; holds nothing for a verified company until a platform admin accepts it for review.</summary>
    Open,

    /// <summary>Accepted for review by a platform admin: holds the company's approvals until it is closed.</summary>
    UnderReview,
}

/// <summary>What a retry of an upheld dispute's identity provider update came to.</summary>
public enum CrDisputeRetry
{
    /// <summary>Every step has now succeeded, and the outcome is recorded; the dispute leaves the console's list.</summary>
    Updated,

    /// <summary>A step failed again, or the outcome could not be recorded; the dispute stays listed.</summary>
    StillFailing,

    /// <summary>
    /// The claimant is no longer the company's vendor admin (a later dispute moved it on), so they were given nothing (and
    /// anything this run granted before it saw the move was taken back); the removed users' failed removals ran, except for
    /// a user who belongs to a vendor company again, and none failed. Recorded and audited; the dispute leaves the list.
    /// </summary>
    Superseded,
}

/// <summary>
/// A pending dispute as the platform console lists it, with the company's current vendor admin as the identity provider
/// knows them (null parts unknown; the name is self-declared). The console lists disputes grouped by company (the company
/// with the oldest pending dispute first), oldest first within it. <see cref="OverCap"/>: five older disputes of the
/// company are still pending now (worked out when listing, so it clears once those close); such a dispute is never
/// refused (the real owner might be the sixth). <see cref="CompanyPending"/>: the company's pending disputes now.
/// </summary>
public sealed record CrDispute(
    Guid Id,
    Guid CompanyId,
    string CrNumber,
    string CompanyNameAr,
    string CompanyNameEn,
    string ClaimantUserId,
    string ClaimantEmail,
    string ClaimantName,
    string Statement,
    Guid RaisedOnTenant,
    DateTimeOffset RaisedAt,
    string? RegistrantUserId,
    OwnershipVerificationMethod? OwnershipVerifiedBy,
    CrDisputeStatus Status = CrDisputeStatus.Open,
    VendorRegistrant? Registrant = null,
    bool OverCap = false,
    int CompanyPending = 1);

/// <summary>
/// An upheld dispute whose identity provider update failed or was never recorded; the console offers a retry.
/// <see cref="FailedSteps"/> names each step that failed last time (for example <c>organization:add:acme</c>, or
/// <c>organization:remove:beta:{user id}</c>); empty when the outcome was never recorded.
/// </summary>
public sealed record CrDisputeIdentityProviderFailure(
    Guid DisputeId, Guid CompanyId, string CrNumber, string CompanyNameAr, string CompanyNameEn, string ClaimantUserId,
    string ClaimantName, IReadOnlyList<string> RemovedUserIds, DateTimeOffset UpheldAt, IReadOnlyList<string> FailedSteps);

/// <summary>
/// An upheld dispute: the company, its new vendor admin, the vendor users removed from it, whether the identity provider
/// took the change too (the claimant's realm role <c>vendor</c> and organizations granted, the removed users' taken back),
/// and whether that outcome was recorded. When it was not (<see cref="IdentityProviderOutcomeRecorded"/> false), the
/// uphold itself stands and the dispute is listed for a retry, since its outcome is unknown.
/// </summary>
public sealed record CrDisputeUpheld(
    Guid CompanyId, string ClaimantUserId, IReadOnlyList<string> RemovedUserIds, bool IdentityProviderUpdated,
    bool IdentityProviderOutcomeRecorded = true);

/// <summary>
/// The platform console's side of W-33: the check method (a platform setting, since vendor identity is global, ADR-0008,
/// and the Wathq subscription is the platform's) and the review of disputes. Works only in a platform request
/// (<c>IPlatformRequestContext</c>), as the acting user (else <see cref="InvalidOperationException"/>); every change is
/// audited in the platform audit. The console calls it under the PlatformAdmin policy.
/// </summary>
public interface ICrOwnershipAdministration
{
    Task<CrOwnershipSettings> GetSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>Sets the method, audited as <c>vendor.ownership_method_changed</c>; returns the method before.</summary>
    Task<Result<CrOwnershipMethod>> SetMethodAsync(CrOwnershipMethod method, string actorId, CancellationToken cancellationToken = default);

    /// <summary>The pending disputes (open and under review), oldest first, with the company's current vendor admin.</summary>
    Task<IReadOnlyList<CrDispute>> ListOpenDisputesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Accepts an open dispute for review (ADR-0013 decision 1): from then on it holds the company's approvals by every
    /// tenant until it is closed. Audited as <c>vendor.dispute_accepted</c>. <see cref="CrDisputeErrors.NotOpen"/> when it is
    /// not open. Throws <see cref="InvalidOperationException"/> when the admin raised the dispute themselves.
    /// </summary>
    Task<Result<Guid>> AcceptForReviewAsync(Guid disputeId, string actorId, CancellationToken cancellationToken = default);

    /// <summary>Upheld disputes whose identity provider update failed or was never recorded, oldest first.</summary>
    Task<IReadOnlyList<CrDisputeIdentityProviderFailure>> ListIdentityProviderFailuresAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Repeats the steps of an upheld dispute's identity provider update that failed (the claimant's realm role
    /// <c>vendor</c> and membership of each tenant's organization the company worked with when the uphold committed, the
    /// removed users' role and memberships taken back). A step already done is never run again, so a membership a tenant
    /// took away after the uphold stays taken away (W-21: only the tenant restores access). Each run's results are merged
    /// into the stored ones and audited as <c>vendor.dispute_identity_provider</c>. When the claimant is no longer the
    /// company's vendor admin nothing runs, and the dispute is recorded and audited as superseded.
    /// </summary>
    Task<CrDisputeRetry> RetryIdentityProviderAsync(Guid disputeId, string actorId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Upholds a pending dispute after the admin checked the claimant against the CR certificate: the company moves to
    /// the claimant as its vendor admin, its vendor users are removed, its ownership is verified (method dispute; the
    /// verification it replaces stays on the dispute) and its other pending disputes close, audited as
    /// <c>vendor.dispute_upheld</c>. Then the identity provider: the claimant gets the realm role <c>vendor</c>, the
    /// removed users lose it, and in the organization of every tenant the company works with the claimant is added and the
    /// removed users are taken out (W-21 no longer lets <c>/vendor/join</c> restore a membership, so this is how the
    /// rightful owner gets access at once). The outcome of every step is stored on the dispute and audited as
    /// <c>vendor.dispute_identity_provider</c>, and a failure stays listed in the console for a retry. Throws
    /// <see cref="InvalidOperationException"/> when the admin raised the dispute themselves.
    /// </summary>
    Task<Result<CrDisputeUpheld>> UpholdAsync(Guid disputeId, string? note, string actorId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rejects a pending dispute; nothing else changes. Audited as <c>vendor.dispute_rejected</c>. Throws
    /// <see cref="InvalidOperationException"/> when the admin raised the dispute themselves.
    /// </summary>
    Task<Result<Guid>> RejectAsync(Guid disputeId, string? note, string actorId, CancellationToken cancellationToken = default);
}

/// <summary>What a claimant types on <c>/vendor/dispute</c>: the CR number, why the company is theirs, and the privacy notice (V-14).</summary>
public sealed record CrDisputeRequest(string? CrNumber, string? Statement, string? AcceptedPrivacyNotice, string? PrivacyNoticeCulture);

/// <summary>One of the signed-in person's own disputes.</summary>
public sealed record OwnCrDispute(Guid Id, string CrNumber, DateTimeOffset RaisedAt, string Status);

/// <summary>
/// The real company's way back (W-33): a signed-in person without a vendor company or a staff role claims the company
/// registered under a CR number; a platform admin reviews it (<see cref="ICrOwnershipAdministration"/>). Works on the
/// tenant of the current request as the acting user, outside any vendor session.
/// </summary>
public interface ICrDisputes
{
    /// <summary>Every field error in form order (CR number, statement, privacy notice); empty when valid.</summary>
    IReadOnlyList<Error> Validate(CrDisputeRequest request);

    /// <summary>
    /// Records an open dispute with the acting user as claimant and <paramref name="email"/> and <paramref name="name"/>
    /// from their verified token, audited as <c>vendor.dispute_raised</c> in the platform audit. A CR number no company
    /// holds is <see cref="CrDisputeErrors.NoCompany"/> and counts toward the duplicate-CR limit (V-6).
    /// </summary>
    Task<Result<Guid>> RaiseAsync(CrDisputeRequest request, string email, string name, CancellationToken cancellationToken = default);

    /// <summary>The acting user's own disputes, newest first.</summary>
    Task<IReadOnlyList<OwnCrDispute>> ListOwnAsync(CancellationToken cancellationToken = default);
}

/// <summary>Stable error codes of the ownership check and the disputes; pages map them to localized text.</summary>
public static class CrOwnershipErrors
{
    public const string Unverified = "vendor.ownership_unverified";
    public const string NoCertificate = "vendor.ownership_no_certificate";
    public const string NoteRequired = "vendor.ownership_note_required";
    public const string Disputed = "vendor.ownership_disputed";
    public const string WathqNotSelected = "vendor.ownership_wathq_not_selected";
}

/// <summary>Stable error codes of disputes.</summary>
public static class CrDisputeErrors
{
    public const string InvalidCrNumber = "dispute.invalid_cr_number";
    public const string StatementRequired = "dispute.statement_required";
    public const string PrivacyNoticeRequired = "dispute.privacy_notice_required";
    public const string NoCompany = "dispute.no_company";
    public const string AlreadyOpen = "dispute.already_open";
    public const string TooManyOpen = "dispute.too_many_open";
    public const string AlreadyVendor = "dispute.already_vendor";
    public const string StaffAccount = "dispute.staff_account";
    public const string NotOpen = "dispute.not_open";
    public const string NoteRequired = "dispute.note_required";

    /// <summary>The person tried too many CR numbers in the last hour (the duplicate-CR limit, V-6).</summary>
    public const string Limited = "dispute.limited";
}
