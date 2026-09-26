namespace Platform.Modules.Operations.Alerts;

/// <summary>One plain-text alert email (F-60). Internal to Operations: no other module sends platform alerts.</summary>
internal sealed record AlertMessage(string Subject, string Body);

internal interface IAlertSender
{
    Task SendAsync(AlertMessage message, CancellationToken cancellationToken = default);
}
