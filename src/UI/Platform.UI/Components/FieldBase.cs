using Microsoft.AspNetCore.Components;

namespace Platform.UI.Components;

/// <summary>
/// What every form field shares (docs/08 section 8): a label tied to the input, help and error text tied through
/// aria-describedby, and aria-invalid while an error shows. Ids are unique per instance.
/// </summary>
public abstract class FieldBase : ComponentBase
{
    [Parameter, EditorRequired] public string Label { get; set; } = string.Empty;

    [Parameter] public string? Help { get; set; }

    /// <summary>A specific message: what is wrong and what to do (docs/08 section 9), never just "invalid".</summary>
    [Parameter] public string? Error { get; set; }

    [Parameter] public bool Required { get; set; }

    [Parameter] public bool Disabled { get; set; }

    [Parameter(CaptureUnmatchedValues = true)] public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; set; }

    protected string Id { get; } = $"field-{Guid.NewGuid():N}";

    protected string HelpId => $"{Id}-help";

    protected string ErrorId => $"{Id}-error";

    /// <summary>The error text currently shown; a derived field may add its own validation message.</summary>
    protected virtual string? ShownError => Error;

    protected string? AriaInvalid => ShownError is null ? null : "true";

    protected string? DescribedBy
    {
        get
        {
            var ids = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(Help))
            {
                ids.Add(HelpId);
            }

            if (ShownError is not null)
            {
                ids.Add(ErrorId);
            }

            return ids.Count == 0 ? null : string.Join(' ', ids);
        }
    }

    protected const string ControlClass =
        "min-h-11 w-full rounded-control border border-ink-muted bg-surface px-3 py-2 text-body text-ink "
        + "disabled:cursor-not-allowed disabled:bg-canvas aria-[invalid=true]:border-danger aria-[invalid=true]:border-2";
}
