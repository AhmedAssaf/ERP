using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Platform.Web.Account;

/// <summary>
/// W-21 in an open Blazor circuit: once <see cref="MembershipRevalidatingStateProvider"/> finds the session no longer
/// stands, <see cref="End"/> sends the browser to a full reload of the current page, whose request is challenged (the
/// cookie's own revalidation or expiry refuses it), so the user lands on the sign-in page; and from then on no inbound
/// activity of the circuit (event, JavaScript callback, navigation) runs, so a client that ignores the reload can do
/// nothing more. When the circuit opens it remembers two things from its connection request: the circuit's own
/// synchronization context (circuit handlers open on the renderer's dispatcher), to which the navigation is posted because
/// revalidation runs on a background thread; and when the connection's cookie expires (<see cref="SessionExpiresAt"/>, from
/// the authentication result the middleware left on the request), so a circuit never outlives its cookie. Outside a
/// circuit (prerendering) nothing was captured and <see cref="End"/> only marks the scope.
/// </summary>
internal sealed partial class CircuitSessionGuard(
    NavigationManager navigation, IHttpContextAccessor httpContextAccessor, ILogger<CircuitSessionGuard> logger) : CircuitHandler
{
    private volatile SynchronizationContext? _circuitContext;
    private volatile bool _ended;

    public override int Order => int.MinValue + 2;

    public bool Ended => _ended;

    /// <summary>When the cookie of the circuit's connection request expires; null when unknown (no cookie ticket).</summary>
    public DateTimeOffset? SessionExpiresAt { get; private set; }

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _circuitContext = SynchronizationContext.Current;
        SessionExpiresAt = httpContextAccessor.HttpContext?.Features.Get<IAuthenticateResultFeature>()?.AuthenticateResult?.Properties?.ExpiresUtc;
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
