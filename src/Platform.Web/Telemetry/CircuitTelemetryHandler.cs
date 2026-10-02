using Microsoft.AspNetCore.Components.Server.Circuits;
using Platform.Shared.Tenancy;
using Platform.Web.Tenancy;

namespace Platform.Web.Telemetry;

/// <summary>
/// O-9 inside a Blazor circuit, whose events run outside any HTTP request: every inbound activity (an event, a navigation, a
/// JavaScript callback) runs inside a log scope with the circuit's tenant id and slug, its user's <c>sub</c> and its vendor
/// company, whichever are set. They are read when the activity runs, long after <see cref="TenantCircuitHandler"/> and
/// <c>VendorCircuitHandler</c> set them as the circuit opened. Ordered last, after the usage handler, so no two handlers share
/// an order: its scope therefore opens inside the session guard's check (an ended circuit runs nothing, and logs nothing)
/// and the usage handler's activity write, whose rare failure warning carries no circuit context.
/// </summary>
internal sealed class CircuitTelemetryHandler(
    ITenantAccessor tenants, IActingUserAccessor actingUser, IVendorAccessor vendor, ILogger<CircuitTelemetryHandler> logger)
    : CircuitHandler
{
    public override int Order => int.MinValue + 4;

    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(Func<CircuitInboundActivityContext, Task> next) =>
        async context =>
        {
            var scope = TelemetryContext.Of(tenants.Current, actingUser.UserId, vendor.Current);
            if (scope.Length == 0)
            {
                await next(context);
                return;
            }

            using (logger.BeginScope(scope))
            {
                await next(context);
            }
        };
}
