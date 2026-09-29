using Microsoft.EntityFrameworkCore;
using Platform.Modules.Vendors.Contracts;
using Platform.Modules.Vendors.Persistence;

namespace Platform.Modules.Vendors.Ownership;

/// <summary>Reads of the W-33 tables and the codes they store (migration 0018).</summary>
internal static class OwnershipStore
{
    /// <summary>Longest officer or platform admin note (the database checks the same).</summary>
    public const int MaxNoteLength = 1000;

    /// <summary>Longest dispute statement (the database checks the same).</summary>
    public const int MaxStatementLength = 2000;

    public static async Task<SettingsRow> SettingsAsync(VendorsDbContext db, CancellationToken cancellationToken) =>
        await db.Database.SqlQuery<SettingsRow>($"select method, changed_by, changed_at from vendor.ownership_settings where id")
            .SingleAsync(cancellationToken);

    public static CrOwnershipMethod Method(string code) => code switch
    {
        "manual" => CrOwnershipMethod.Manual,
        "wathq" => CrOwnershipMethod.Wathq,
        _ => throw new InvalidOperationException($"Unknown ownership check method '{code}'."),
    };

    public static string Code(CrOwnershipMethod method) => method switch
    {
        CrOwnershipMethod.Manual => "manual",
        CrOwnershipMethod.Wathq => "wathq",
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unknown ownership check method."),
    };

    public static OwnershipVerificationMethod? VerificationMethod(string? code) => code switch
    {
        null => null,
        "manual" => OwnershipVerificationMethod.Manual,
        "wathq" => OwnershipVerificationMethod.Wathq,
        "dispute" => OwnershipVerificationMethod.Dispute,
        _ => throw new InvalidOperationException($"Unknown ownership verification method '{code}'."),
    };

    /// <summary>The lookup outcome as the audit entry names it.</summary>
    public static string Code(CrLookupOutcome outcome) => outcome switch
    {
        CrLookupOutcome.Manual => "manual",
        CrLookupOutcome.Found => "found",
        CrLookupOutcome.NotConfigured => "not_configured",
        CrLookupOutcome.Unavailable => "unavailable",
        CrLookupOutcome.NotFound => "not_found",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown lookup outcome."),
    };

    internal sealed class SettingsRow
    {
        public string Method { get; set; } = string.Empty;

        public string? ChangedBy { get; set; }

        public DateTimeOffset ChangedAt { get; set; }
    }
}
