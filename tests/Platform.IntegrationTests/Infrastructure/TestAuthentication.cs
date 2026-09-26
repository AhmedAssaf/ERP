using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>A signed-in user for web tests, sent as a header and turned into the claims Keycloak issues.</summary>
internal sealed record TestUser(string Subject, IReadOnlyList<string> Organizations, string? Locale = null)
{
    public const string Header = "X-Test-User";

    public static TestUser AcmeAdmin { get; } = new("acme.admin", ["acme"], "ar");

    public static TestUser BetaAdmin { get; } = new("beta.admin", ["beta"], "en");
}

internal sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(TestUser.Header, out var raw))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var user = JsonSerializer.Deserialize<TestUser>(raw.ToString())!;
        var claims = new List<Claim> { new("sub", user.Subject), new("preferred_username", user.Subject) };
        claims.AddRange(user.Organizations.Select(o => new Claim("organization", o)));
        if (user.Locale is not null)
        {
            claims.Add(new Claim("locale", user.Locale));
        }

        var identity = new ClaimsIdentity(claims, SchemeName, "preferred_username", null);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

internal static class TestAuthenticationExtensions
{
    public static IServiceCollection AddTestAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        services.Configure<AuthenticationOptions>(o =>
        {
            o.DefaultScheme = TestAuthHandler.SchemeName;
            o.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
            o.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            o.DefaultForbidScheme = TestAuthHandler.SchemeName;
        });
        return services;
    }

    public static HttpRequestMessage As(this HttpRequestMessage request, TestUser user)
    {
        request.Headers.Add(TestUser.Header, JsonSerializer.Serialize(user));
        return request;
    }
}
