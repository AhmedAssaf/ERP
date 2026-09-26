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

/// <summary>Tenant: the tenant's logo, name and primary colour. Platform: the WaslaBid mark for the platform console.</summary>
public enum AppShellVariant
{
    Tenant,
    Platform,
}

public sealed record SelectOption(string Value, string Text);

/// <summary>One line of an <see cref="AuditList"/>: who did what, when, with an optional detail line.</summary>
public sealed record AuditEntry(string Actor, string Action, DateTimeOffset At, string? Detail = null);

public sealed record ToastMessage(Guid Id, string Text, ToastTone Tone);
