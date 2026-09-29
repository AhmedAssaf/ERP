using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Platform.Web.Account;

/// <summary>
/// W-21 in an open Blazor circuit: once <see cref="MembershipRevalidatingStateProvider"/> finds the session no longer
/// stands, <see cref="End"/> sends the browser one full reload of the current page, whose request is challenged or shows
/// the access-removed page (the cookie's own revalidation or expiry refuses it); from then on no inbound activity of the
/// circuit (event, JavaScript callback, navigation, render acknowledgement) runs, and none of it triggers another reload:
/// the browser's answer to the reload's own JavaScript call is inbound activity too, and answering it with a new reload
/// looped (QA D1). If the circuit is still connected <see cref="DisconnectAfter"/> after the reload (a client that ignores
/// it), its connection is aborted, so the circuit ends instead of lingering.
/// <para>
/// When the circuit opens it remembers from its connection request: the circuit's own synchronization context (circuit
/// handlers open on the renderer's dispatcher), to which the reload is posted because revalidation runs on a background
/// thread; the connection itself, to abort; and when the connection's cookie expires (<see cref="SessionExpiresAt"/>, from
/// the authentication result the middleware left on the request), so a circuit never outlives its cookie. Outside a
/// circuit (prerendering) nothing was captured and <see cref="End"/> only marks the scope.
/// </para>
/// </summary>
internal sealed partial class CircuitSessionGuard : CircuitHandler
{
    internal static readonly TimeSpan DisconnectAfter = TimeSpan.FromSeconds(10);

    private readonly NavigationManager _navigation;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<CircuitSessionGuard> _logger;
    private readonly TimeSpan _disconnectAfter;
    private volatile SynchronizationContext? _circuitContext;
    private volatile HttpContext? _connection;
    private volatile bool _ended;
    private volatile bool _connected;
    private int _reloadSent;

    public CircuitSessionGuard(NavigationManager navigation, IHttpContextAccessor httpContextAccessor, ILogger<CircuitSessionGuard> logger)
        : this(navigation, httpContextAccessor, logger, DisconnectAfter)
    {
    }

    internal CircuitSessionGuard(
        NavigationManager navigation, IHttpContextAccessor httpContextAccessor, ILogger<CircuitSessionGuard> logger, TimeSpan disconnectAfter)
    {
        _navigation = navigation;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
        _disconnectAfter = disconnectAfter;
    }

    public override int Order => int.MinValue + 2;

    public bool Ended => _ended;

    /// <summary>When the cookie of the circuit's connection request expires; null when unknown (no cookie ticket).</summary>
    public DateTimeOffset? SessionExpiresAt { get; private set; }

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _circuitContext = SynchronizationContext.Current;
        _connection = _httpContextAccessor.HttpContext;
        SessionExpiresAt = _connection?.Features.Get<IAuthenticateResultFeature>()?.AuthenticateResult?.Properties?.ExpiresUtc;
        return Task.CompletedTask;
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _connected = true;
        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _connected = false;
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _connected = false;
        return Task.CompletedTask;
    }

    /// <summary>Ends the circuit's session: one reload, then every inbound activity refused. Later calls change nothing.</summary>
    public void End()
    {
        _ended = true;
        if (Interlocked.Exchange(ref _reloadSent, 1) == 0 && _circuitContext is { } circuitContext)
        {
            circuitContext.Post(_ => Reload(), null);
            _ = DisconnectIfStillConnectedAsync();
        }
    }

    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(Func<CircuitInboundActivityContext, Task> next) =>
        context => _ended ? Task.CompletedTask : next(context);

    private void Reload()
    {
        try
        {
            _navigation.NavigateTo(_navigation.Uri, forceLoad: true, replace: true);
        }
        catch (InvalidOperationException ex)
        {
            // The circuit is already going away (no JavaScript runtime left); its requests are refused anyway.
            ReloadFailed(_logger, ex.GetType().Name);
        }
    }

    private async Task DisconnectIfStillConnectedAsync()
    {
        await Task.Delay(_disconnectAfter).ConfigureAwait(false);
        if (_connected && _connection is { } connection)
        {
            Disconnecting(_logger);
            connection.Abort();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The circuit of an ended session could not be reloaded ({ErrorType}).")]
    private static partial void ReloadFailed(ILogger logger, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The circuit of an ended session was still connected after its reload; its connection is aborted.")]
    private static partial void Disconnecting(ILogger logger);
}
