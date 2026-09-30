using Microsoft.AspNetCore.Components.Server.Circuits;
using Platform.Modules.Identity.Contracts;
using Platform.Shared.Tenancy;
using Platform.Web.Account;

namespace Platform.Web.Usage;

/// <summary>
/// Concurrent users (spec 6.3, O-20): keeps this circuit in <see cref="ConnectedCircuits"/> while its connection is up.
/// Ordered after <see cref="CircuitSessionGuard"/>, so the tenant (<c>TenantCircuitHandler</c>), the acting user and the
/// vendor context (<c>VendorCircuitHandler</c>) are already set when it classifies the circuit (<see cref="UsageKinds"/>).
/// The principal of a circuit is fixed for its life, so it is classified once, from the connection request of the first
/// connection. A disconnected circuit held for reconnection does not count; a reconnection counts again; a circuit whose
/// session W-21 ended does not count, and a reconnection of it does not count either. One handler per circuit scope, so
/// the handler itself is the circuit's key in the registry.
/// <para>
/// Active users (spec 6.4): every inbound activity of a counted staff or vendor circuit (an event, a navigation) records
/// the circuit's user as active through <see cref="IUserActivityRecorder"/>, at most once an hour, so a user working in one
/// long circuit counts; the recorder never throws, and the activity itself always runs.
/// </para>
/// </summary>
internal sealed class UsageCircuitHandler(
    IHttpContextAccessor httpContextAccessor,
    ITenantAccessor tenants,
    IVendorAccessor vendor,
    IPlatformRequestContext platform,
    CircuitSessionGuard guard,
    ConnectedCircuits registry,
    IUserActivityRecorder activity) : CircuitHandler
{
    private bool _classified;
    private UsageKind? _kind;
    private string _userId = string.Empty;

    public override int Order => int.MinValue + 3;

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        if (!guard.Ended && Classify() is { } kind)
        {
            registry.Add(this, tenants.Current?.Slug, kind, _userId, () => guard.Ended);
        }

        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        registry.Remove(this);
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        registry.Remove(this);
        return Task.CompletedTask;
    }

    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(Func<CircuitInboundActivityContext, Task> next) =>
        async context =>
        {
            if (!guard.Ended && Classify() is UsageKind.Staff or UsageKind.Vendor)
            {
                await activity.RecordAsync(_kind == UsageKind.Vendor ? ActivityKind.Vendor : ActivityKind.Staff, CancellationToken.None);
            }

            await next(context);
        };

    /// <summary>The circuit's kind, or null when it is not counted; classified once, on the first connection request seen.</summary>
    private UsageKind? Classify()
    {
        if (!_classified)
        {
            if (httpContextAccessor.HttpContext?.User is not { } user)
            {
                // No connection request to read the principal from: not counted now, asked again on the next connection.
                return null;
            }

            _kind = UsageKinds.Of(user, tenants, vendor, platform);
            _userId = user.FindFirst(IdentityClaims.Subject)?.Value ?? string.Empty;
            _classified = true;
        }

        return _kind;
    }
}
