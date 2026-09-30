using Platform.Modules.Operations.Contracts;

namespace Platform.Modules.Operations.Alerts;

/// <summary>Other modules' platform alerts (<see cref="IPlatformAlerts"/>) through the F-60 sender.</summary>
internal sealed class PlatformAlerts(IAlertSender sender) : IPlatformAlerts
{
    public Task SendAsync(PlatformAlert alert, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alert);
        return sender.SendAsync(new AlertMessage(alert.Subject, alert.Body), cancellationToken);
    }
}
