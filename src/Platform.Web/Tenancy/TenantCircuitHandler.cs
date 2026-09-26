using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Platform.Modules.Tenancy.Contracts;
using Platform.Shared.Tenancy;

namespace Platform.Web.Tenancy;

/// <summary>
/// A Blazor circuit has its own DI scope, separate from the HTTP request that started it. This handler sets the
/// circuit's tenant from the host name when the circuit opens, before any component renders.
/// </summary>
internal sealed class TenantCircuitHandler(NavigationManager navigation, ITenantDirectory directory, TenantAccessor accessor) : CircuitHandler
{
    public override async Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var host = new Uri(navigation.BaseUri).Host;
        var tenant = await directory.FindByHostAsync(host, cancellationToken)
            ?? throw new InvalidOperationException($"No tenant owns the host '{host}'.");
        accessor.Set(tenant);
    }
}
