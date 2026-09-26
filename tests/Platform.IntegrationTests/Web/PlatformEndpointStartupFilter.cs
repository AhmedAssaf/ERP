using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// Maps two test-only endpoints under <c>/platform</c> before the console pages exist (plan task 6): one with
/// <c>RequireAuthorization()</c> (the default policy) and one without authorization metadata (the fallback policy).
/// Both answer with the caller's <c>acr</c> claim.
/// </summary>
internal sealed class PlatformEndpointStartupFilter : IStartupFilter
{
    public const string DefaultPolicyPath = "/platform/test/default";
    public const string FallbackPolicyPath = "/platform/test/fallback";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        next(app);
        var endpoints = app.Properties.TryGetValue("__EndpointRouteBuilder", out var value) && value is IEndpointRouteBuilder routeBuilder
            ? routeBuilder
            : throw new InvalidOperationException("The host did not expose its endpoint route builder.");
        endpoints.MapGet(DefaultPolicyPath, (ClaimsPrincipal user) => Results.Text(user.FindFirst("acr")?.Value)).RequireAuthorization();
        endpoints.MapGet(FallbackPolicyPath, (ClaimsPrincipal user) => Results.Text(user.FindFirst("acr")?.Value));
    };
}
