namespace Platform.UI.Components;

/// <summary>
/// The toasts of one circuit (scoped). A page calls <see cref="Show"/> after an action with the same verb as the button
/// that caused it ("Published", "Scores locked"); <see cref="ToastRegion"/> displays them. Only the newest
/// <see cref="MaxVisible"/> are kept; each stays until dismissed or pushed out by newer ones.
/// </summary>
public sealed class ToastService
{
    public const int MaxVisible = 3;

    private readonly List<ToastMessage> _messages = [];

    public event Action? Changed;

    public IReadOnlyList<ToastMessage> Messages => _messages;

    public void Show(string message, ToastTone tone = ToastTone.Success)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        _messages.Add(new ToastMessage(Guid.NewGuid(), message, tone));
        if (_messages.Count > MaxVisible)
        {
            _messages.RemoveRange(0, _messages.Count - MaxVisible);
        }

        Changed?.Invoke();
    }

    public void Dismiss(Guid id)
    {
        if (_messages.RemoveAll(m => m.Id == id) > 0)
        {
            Changed?.Invoke();
        }
    }
}
