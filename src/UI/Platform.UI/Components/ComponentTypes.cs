namespace Platform.UI.Components;

/// <summary>docs/08 section 6: primary for continue, submit, approve; danger for close, cancel, reject, discard.</summary>
public enum ButtonVariant
{
    Primary,
    Secondary,
    Danger,
    Quiet,
}

/// <summary>Colour of a <see cref="StatusBadge"/>; the text always carries the meaning, the colour only supports it.</summary>
public enum StatusTone
{
    Neutral,
    Info,
    Success,
    Warning,
    Danger,
}

/// <summary>One line of a <see cref="StatTile"/>'s split: a part's label and its number (null when unknown).</summary>
public sealed record StatTilePart(string Label, int? Value);

public enum ToastTone
{
    Success,
    Info,
    Danger,
}

public enum TableDensity
{
    Comfortable,
    Compact,
}

/// <summary>
/// Tenant: the tenant's logo, name and primary colour. Platform: the WaslaBid mark for the platform console. Vendor: the
/// tenant's brand as in Tenant (F-02), named as the tenant's supplier portal, for a vendor's pages on the tenant host.
/// </summary>
public enum AppShellVariant
{
    Tenant,
    Platform,
    Vendor,
}

public sealed record SelectOption(string Value, string Text);

/// <summary>One line of an <see cref="AuditList"/>: who did what, when, with an optional detail line.</summary>
public sealed record AuditEntry(string Actor, string Action, DateTimeOffset At, string? Detail = null);

public sealed record ToastMessage(Guid Id, string Text, ToastTone Tone);

/// <summary>Where a <see cref="FileUpload"/> stands; <see cref="FileUploadStatus"/> shows each one.</summary>
public enum FileUploadState
{
    /// <summary>Nothing sent yet (a file may be chosen).</summary>
    Idle,

    /// <summary>Chunks are being sent; the percentage shows.</summary>
    Uploading,

    /// <summary>Every chunk arrived; the server is assembling and virus-scanning the file.</summary>
    Scanning,

    /// <summary>Stored, but the virus scanner gave no verdict yet: listed once a later scan finds it clean.</summary>
    Pending,

    /// <summary>Scanned clean and stored.</summary>
    Done,

    /// <summary>Refused for a reason retrying cannot fix (type, size, a virus, too many uploads): choose another file.</summary>
    Rejected,

    /// <summary>The connection failed after the retries; the upload can resume from the chunk that failed.</summary>
    Failed,
}

/// <summary>What the file chooser holds, as the script reads it: name, size in bytes and the browser's content type.</summary>
public sealed record FileUploadSelection(string Name, long Size, string Type);

/// <summary>
/// What <see cref="FileUpload"/> asks its script to send: the upload API's base URL, the document type, the expiry date
/// (yyyy-MM-dd, or null), and the antiforgery token with the header that carries it. <see cref="Key"/> names the upload
/// in the script, so a retry resumes the same one.
/// </summary>
public sealed record FileUploadRequest(string Key, string Endpoint, string DocumentType, string? ExpiresOn, string? Token, string TokenHeader);

/// <summary>
/// How an upload ended, as the script reports it. <see cref="Kind"/> is one of the constants below; a rejection carries
/// the server's error code (or one of the script's own); a failure carries the chunk to resume from (the chunk count when
/// only the completion is left).
/// </summary>
public sealed record FileUploadOutcome(string Kind, string? Code = null, int ResumeFrom = 0, string? DocumentId = null)
{
    public const string Done = "done";
    public const string Pending = "pending";
    public const string Rejected = "rejected";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    /// <summary>The script's code for 401 or 403: the session ended or the user may no longer upload.</summary>
    public const string SignedOut = "signed_out";

    /// <summary>The script's code for a refusal without an error code (an HTTP status alone, such as 413).</summary>
    public const string Refused = "refused";
}

/// <summary>A finished upload: the new document's id, and whether its virus scan is still pending.</summary>
public sealed record FileUploadCompleted(string DocumentId, bool ScanPending);
