using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Tenancy;

namespace Platform.UI;

public static class PlatformLocalization
{
    public const string DefaultCulture = "ar-SA";

    public static IReadOnlyList<string> SupportedCultures { get; } = ["ar-SA", "en-US"];

    /// <summary>Culture order (spec section 7): the culture cookie, the "locale" claim, the tenant default, then ar-SA.</summary>
    public static IServiceCollection AddPlatformLocalization(this IServiceCollection services)
    {
        services.AddLocalization(o => o.ResourcesPath = "Resources");
        services.Configure<RequestLocalizationOptions>(o =>
        {
            o.SetDefaultCulture(DefaultCulture)
                .AddSupportedCultures([.. SupportedCultures])
                .AddSupportedUICultures([.. SupportedCultures]);
            o.RequestCultureProviders =
            [
                new CookieRequestCultureProvider(),
                new LocaleClaimCultureProvider(),
                new TenantDefaultCultureProvider(),
            ];
        });
        return services;
    }

    /// <summary>Maps "ar", "ar-SA", "en", "en-US" in any case to a supported culture; anything else to null.</summary>
    public static string? Normalize(string? culture)
    {
        if (culture is null)
        {
            return null;
        }

        if (culture.Equals("ar", StringComparison.OrdinalIgnoreCase) || culture.Equals("ar-SA", StringComparison.OrdinalIgnoreCase))
        {
            return "ar-SA";
        }

        if (culture.Equals("en", StringComparison.OrdinalIgnoreCase) || culture.Equals("en-US", StringComparison.OrdinalIgnoreCase))
        {
            return "en-US";
        }

        return null;
    }

    private static Task<ProviderCultureResult?> ResultFor(string? culture) =>
        Task.FromResult(Normalize(culture) is { } c ? new ProviderCultureResult(c) : null);

    /// <summary>Keycloak's "locale" claim, from the user's profile.</summary>
    private sealed class LocaleClaimCultureProvider : RequestCultureProvider
    {
        public override Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext) =>
            ResultFor(httpContext.User.FindFirst("locale")?.Value);
    }

    private sealed class TenantDefaultCultureProvider : RequestCultureProvider
    {
        public override Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext) =>
            ResultFor(httpContext.RequestServices.GetService<ITenantAccessor>()?.Current?.DefaultCulture);
    }
}
