using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Platform.Modules.Tenancy.Contracts;
using Platform.Shared.Tenancy;
using Platform.Web.PlatformHost;

namespace Platform.Web.Tenancy;

/// <summary>
/// A Blazor circuit has its own DI scope, separate from the HTTP request that started it. This handler sets the
/// circuit's tenant when the circuit opens, before any other circuit handler runs and before any component renders.
/// The host comes from the <c>/_blazor</c> connection request, which already passed <see cref="TenantMiddleware"/> and
/// authorization. <see cref="NavigationManager.BaseUri"/> is supplied by the browser in the circuit start message, so
/// it is only checked against the connection host, never trusted on its own. A circuit on the platform console's host
/// (its connection request was marked by <see cref="PlatformHostMiddleware"/>) has no tenant and is marked as a
/// platform scope (<see cref="PlatformRequestContext"/>).
/// </summary>
internal sealed class TenantCircuitHandler(
    NavigationManager navigation,
    IHttpContextAccessor httpContextAccessor,
    ITenantDirectory directory,
    TenantAccessor accessor,
    PlatformRequestContext platform) : CircuitHandler
{
    public override int Order => int.MinValue;

    public override async Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var connectionHost = httpContextAccessor.HttpContext?.Request.Host.Host;
        if (string.IsNullOrEmpty(connectionHost))
        {
            throw new InvalidOperationException("The circuit has no connection request to take the tenant host from.");
        }

        var baseUriHost = new Uri(navigation.BaseUri).Host;
        if (!string.Equals(baseUriHost, connectionHost, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The circuit base URI host '{baseUriHost}' does not match the connection host '{connectionHost}'.");
        }

        if (PlatformRequest.IsPlatform(httpContextAccessor.HttpContext!))
        {
            // The circuit's scope is not the connection request's, so its platform mark is set here, as its tenant is below.
            platform.MarkPlatform();
            return;
        }

        var tenant = await directory.FindByHostAsync(connectionHost, cancellationToken)
            ?? throw new InvalidOperationException($"No tenant owns the host '{connectionHost}'.");
        accessor.Set(tenant);
    }
}
