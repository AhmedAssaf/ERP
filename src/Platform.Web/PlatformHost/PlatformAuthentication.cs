using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Platform.Modules.Identity;
using Platform.Modules.Identity.Contracts;
using Platform.Web.Account;

namespace Platform.Web.PlatformHost;

/// <summary>
/// The platform console's sign-in (spec 3.1, D-1, D-2): OpenID Connect against realm <c>waslabid-platform</c> with its
/// own cookie, separate from the tenant scheme in every respect (cookie name and data protection purpose, callback
/// paths, realm and client).
/// </summary>
internal static class PlatformAuthentication
{
    public const string CookieScheme = "PlatformCookie";
    public const string OidcScheme = "PlatformOidc";
    public const string CookieName = "waslabid.platform";
    public const string PolicyName = "PlatformAdmin";
    public const string ConfigurationSection = "PlatformOidc";
    public const string CallbackPath = "/signin-platform";
    public const string SignedOutCallbackPath = "/signout-callback-platform";
    public const string RemoteSignOutPath = "/signout-platform";

    /// <summary>The console's sign-out (POST with an antiforgery token); the realm's end-session returns to <see cref="HomePath"/>.</summary>
    public const string SignOutPath = "/platform/sign-out";
    public const string HomePath = "/platform";

    /// <summary>PlatformAdmin: the Identity module's requirements, authenticated with the platform cookie only.</summary>
    public static AuthorizationPolicy AdminPolicy { get; } = new AuthorizationPolicyBuilder(CookieScheme)
        .Combine(IdentityModule.PlatformAdminRequirements)
        .Build();

    public static AuthenticationBuilder AddPlatformAuthentication(this AuthenticationBuilder builder, IConfiguration configuration) =>
        builder
            .AddCookie(CookieScheme, options =>
            {
                // Same hardening as the tenant cookie (Secure, HttpOnly, no sliding) with half its lifetime: a console
                // session reaches every tenant, so it lasts a fixed 15 minutes and the admin signs in again with OTP.
                options.Cookie.Name = CookieName;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.HttpOnly = true;
                options.ExpireTimeSpan = TimeSpan.FromMinutes(15);
                options.SlidingExpiration = false;
                options.ForwardChallenge = OidcScheme;
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            })
            .AddOpenIdConnect(OidcScheme, options =>
            {
                configuration.GetSection(ConfigurationSection).Bind(options);
                options.SignInScheme = CookieScheme;
                options.CallbackPath = CallbackPath;
                options.SignedOutCallbackPath = SignedOutCallbackPath;
                options.RemoteSignOutPath = RemoteSignOutPath;
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.UsePkce = true;
                options.MapInboundClaims = false;
                options.GetClaimsFromUserInfoEndpoint = false;
                options.SaveTokens = false;
                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("profile");
                options.TokenValidationParameters.NameClaimType = IdentityClaims.Username;
                options.TokenValidationParameters.RoleClaimType = IdentityClaims.Roles;
                // The handler's default claim actions delete acr from the principal; the PlatformAdmin policy needs it.
                options.ClaimActions.Remove(IdentityClaims.Acr);
                options.Events.OnRedirectToIdentityProvider = context =>
                {
                    // The client's minimum.acr.value already forces the OTP step; asking for it as well keeps the request
                    // explicit if that attribute is ever lost. The policy checks the resulting acr either way.
                    context.ProtocolMessage.AcrValues = IdentityModule.PlatformMinimumAcr.ToString(CultureInfo.InvariantCulture);
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToIdentityProviderForSignOut = SignOutEndpoints.NameClientOnEndSession;
            });
}
