using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Platform.Web.Account;

/// <summary>
/// W-21 in an open Blazor circuit: once <see cref="MembershipRevalidatingStateProvider"/> finds the session no longer
/// stands, <see cref="End"/> sends the browser to a full reload of the current page, whose request is challenged (the
/// cookie's own revalidation refuses it), so the user lands on the sign-in page; and from then on no inbound activity of
/// the circuit (event, JavaScript callback, navigation) runs, so a client that ignores the reload can do nothing more.
/// The navigation is posted to the circuit's own synchronization context, captured when the circuit opened (circuit
/// handlers open on the renderer's dispatcher), because revalidation runs on a background thread. Outside a circuit
/// (prerendering) nothing was captured and <see cref="End"/> only marks the scope.
/// </summary>
internal sealed partial class CircuitSessionGuard(NavigationManager navigation, ILogger<CircuitSessionGuard> logger) : CircuitHandler
{
    private volatile SynchronizationContext? _circuitContext;
    private volatile bool _ended;

    public override int Order => int.MinValue + 2;

    public bool Ended => _ended;

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _circuitContext = SynchronizationContext.Current;
        return Task.CompletedTask;
    }

    public void End()
    {
        _ended = true;
        ReloadForSignIn();
    }

    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(Func<CircuitInboundActivityContext, Task> next) =>
        context =>
        {
            if (!_ended)
            {
                return next(context);
            }

            ReloadForSignIn();
            return Task.CompletedTask;
        };

    private void ReloadForSignIn()
    {
        if (_circuitContext is not { } circuitContext)
        {
            return;
        }

        circuitContext.Post(
            _ =>
            {
                try
                {
                    navigation.NavigateTo(navigation.Uri, forceLoad: true, replace: true);
                }
                catch (InvalidOperationException ex)
                {
                    // The circuit is already going away (no JavaScript runtime left); its requests are refused anyway.
                    ReloadFailed(logger, ex.GetType().Name);
                }
            },
            null);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The circuit of an ended session could not be reloaded ({ErrorType}).")]
    private static partial void ReloadFailed(ILogger logger, string errorType);
}
