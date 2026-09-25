using Elsa.Studio.Contracts;
using Elsa.Studio.Services;
using MudBlazor;

namespace ElsaStudioSpike;

// A tenant's brand colour applied to Studio through its theme hook. In the product this would
// be resolved per request from the tenant's branding (F-02); here it is one fixed teal.
public class TenantThemeProvider : IThemeProvider
{
    public const string Id = "tenant";

    public MudTheme GetTheme()
    {
        var theme = new ClassicThemeProvider().GetTheme();
        theme.PaletteLight.Primary = "#0F766E";
        theme.PaletteLight.AppbarBackground = "#0F766E";
        theme.PaletteDark.Primary = "#14B8A6";
        theme.Typography.Default.FontFamily = ["IBM Plex Sans Arabic", "Tahoma", "sans-serif"];
        return theme;
    }
}

// Studio's App requires this even with authorization disabled; the spike has no login.
public class NoLoginProvider : IUnauthorizedComponentProvider
{
    public Microsoft.AspNetCore.Components.RenderFragment GetUnauthorizedComponent() => b => b.AddContent(0, "No login in the spike.");
}

// Also assumed by Studio's workflow module; no tokens to attach in the spike.
public class NoAuthConnectionConfigurator : Elsa.Studio.Authentication.Abstractions.Contracts.IHttpConnectionOptionsConfigurator
{
    public Task ConfigureAsync(Microsoft.AspNetCore.Http.Connections.Client.HttpConnectionOptions options, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
