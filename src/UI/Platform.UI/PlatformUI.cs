using Microsoft.Extensions.DependencyInjection;
using Platform.UI.Components;

namespace Platform.UI;

public static class PlatformUI
{
    /// <summary>Services the shared components need: one toast queue per circuit or request.</summary>
    public static IServiceCollection AddPlatformUI(this IServiceCollection services)
    {
        services.AddScoped<ToastService>();
        return services;
    }
}
