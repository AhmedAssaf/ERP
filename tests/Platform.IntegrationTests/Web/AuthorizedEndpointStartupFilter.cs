using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// Maps a test-only endpoint with <c>RequireAuthorization()</c>, which uses the default policy rather than the fallback.
/// The host's pipeline is built first; the endpoint is then added to the application's route builder before the first request.
/// </summary>
internal sealed class AuthorizedEndpointStartupFilter : IStartupFilter
{
    public const string Path = "/test/authorized";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        next(app);
        var endpoints = app.Properties.TryGetValue("__EndpointRouteBuilder", out var value) && value is IEndpointRouteBuilder routeBuilder
            ? routeBuilder
            : throw new InvalidOperationException("The host did not expose its endpoint route builder.");
        endpoints.MapGet(Path, () => Results.Ok()).RequireAuthorization();
    };
}
