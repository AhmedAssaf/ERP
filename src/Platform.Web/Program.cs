using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Platform.Modules.Audit;
using Platform.Modules.Identity;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Operations;
using Platform.Modules.Tenancy;
using Platform.Modules.Vendors;
using Platform.Modules.Vendors.Contracts;
using Platform.Modules.Workflow;
using Platform.Shared;
using Platform.Shared.Jobs;
using Platform.UI;
using Platform.Web.Account;
using Platform.Web.Branding;
using Platform.Web.Components;
using Platform.Web.Localization;
using Platform.Web.PlatformHost;
using Platform.Web.Tenancy;
using Platform.Web.Vendor;

var builder = WebApplication.CreateBuilder(args);
var platformDb = builder.Configuration.GetConnectionString("Platform");
if (string.IsNullOrWhiteSpace(platformDb))
{
    throw new InvalidOperationException("Connection string 'Platform' is not configured. Set it with dotnet user-secrets (see README).");
}

if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing"))
{
    foreach (var key in new[]
    {
        "Oidc:Authority", "Oidc:ClientSecret", "Platform:Host", "PlatformOidc:Authority", "PlatformOidc:ClientSecret",
        "KeycloakAdmin:BaseUrl", "KeycloakAdmin:ClientSecret", "KeycloakAdmin:TenantUrl",
    })
    {
        if (string.IsNullOrWhiteSpace(builder.Configuration[key]))
        {
            throw new InvalidOperationException($"Setting '{key}' is not configured. It is required outside Development.");
        }
    }
}

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddPlatformShared();
// One bucket for every module (settings ObjectStorage:*): tenant logos (F-02) and storage usage in the console (F-54).
builder.Services.AddObjectStorage(builder.Configuration);
builder.Services.AddAuditModule(platformDb);
builder.Services.AddTenancyModule(platformDb);
builder.Services.AddIdentityModule(platformDb);
// F-06: staff invitations and organization member counts through the Keycloak Admin API (settings KeycloakAdmin:*).
builder.Services.AddKeycloakAdmin(builder.Configuration);
builder.Services.AddWorkflowModule(platformDb);
builder.Services.AddOperationsModule(platformDb);
// Vendor slice (ADR-0008): one vendor company across tenants.
builder.Services.AddVendorsModule(platformDb);
// Vendor pages (F-11): registration, the Vendor policy's company check, and the vendor context after that policy passes.
builder.Services.AddVendorPortal();
builder.Services.AddScoped<VendorContextResolver>();
// The web host only enqueues and reads jobs (D-6): Hangfire storage without a server, plus the dashboard (task 7).
builder.Services.AddJobClient(platformDb);
builder.Services.AddJobsDashboard();
builder.Services.AddOperationsConsole(builder.Configuration);
builder.Services.AddScoped<TenantOverview>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CircuitHandler, TenantCircuitHandler>();
builder.Services.AddScoped<CircuitHandler, VendorCircuitHandler>();
builder.Services.Configure<PlatformHostOptions>(builder.Configuration.GetSection(PlatformHostOptions.Section));

// Two sign-ins side by side (spec 3.1): tenant hosts use the tenant realm and cookie, the platform host the platform
// realm and its own cookie. The default scheme only forwards, by the host mark PlatformHostMiddleware sets, so neither
// cookie is ever read on the other kind of host.
const string hostScheme = "ByHost";
builder.Services
    .AddAuthentication(hostScheme)
    .AddPolicyScheme(hostScheme, null, options => options.ForwardDefaultSelector = context =>
        PlatformRequest.IsPlatform(context) ? PlatformAuthentication.CookieScheme : CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "waslabid.auth";
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.HttpOnly = true;
        // Fixed 30-minute lifetime, no sliding, until W-21 revalidates membership against Keycloak.
        options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
        options.SlidingExpiration = false;
        options.ForwardChallenge = OpenIdConnectDefaults.AuthenticationScheme;
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    })
    .AddOpenIdConnect(options =>
    {
        builder.Configuration.GetSection("Oidc").Bind(options);
        options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.UsePkce = true;
        options.MapInboundClaims = false;
        options.GetClaimsFromUserInfoEndpoint = false;
        options.SaveTokens = false;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("organization");
        // email and email_verified: a member row seeded by email is bound to the user on first sign-in (F-07 dev seed).
        options.Scope.Add("email");
        options.TokenValidationParameters.NameClaimType = IdentityClaims.Username;
        options.Events.OnRedirectToIdentityProviderForSignOut = SignOutEndpoints.NameClientOnEndSession;
        // F-11: /vendor/register goes to the realm's registration endpoint (VendorRegistrationEndpoints).
        options.Events.OnRedirectToIdentityProvider = VendorRegistrationEndpoints.UseRegistrationEndpoint;
    })
    .AddPlatformAuthentication(builder.Configuration);
builder.Services.AddAuthorization(options =>
{
    // Fallback covers endpoints with no metadata; default covers [Authorize] and RequireAuthorization(). Both are the
    // tenant staff policy here (same tenant, never the realm role vendor), so a vendor opens no staff page that forgot
    // its policy; HostAwareAuthorizationPolicyProvider swaps in PlatformAdmin on the platform host.
    options.FallbackPolicy = IdentityModule.TenantStaffPolicy;
    options.DefaultPolicy = IdentityModule.TenantStaffPolicy;
    options.AddPolicy(PlatformAuthentication.PolicyName, PlatformAuthentication.AdminPolicy);
    // F-07: TenantAdmin, ContractsOfficer, TechnicalEvaluator, FinanceApprover (same tenant plus the role in identity.members).
    options.AddTenantRolePolicies();
    // Vendor pages (spec section 3, V-3): the vendor's own requirements plus membership of the host tenant's organization.
    options.AddPolicy(VendorPolicies.Vendor, new AuthorizationPolicyBuilder()
        .Combine(IdentityModule.SameTenantPolicy)
        .Combine(VendorsModule.VendorRequirements)
        .Build());
    options.AddPolicy(VendorPolicies.VendorApplicant, VendorsModule.VendorApplicantPolicy);
});
builder.Services.AddSingleton<IAuthorizationPolicyProvider, HostAwareAuthorizationPolicyProvider>();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddPlatformLocalization();
builder.Services.AddPlatformUI();

if (builder.Environment.IsDevelopment())
{
    // Caddy on the local Compose stack terminates TLS and forwards the scheme. Production trusts only its own proxy (W-11).
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });
}

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseForwardedHeaders();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler();
    app.UseHsts();
}

app.UseMiddleware<PlatformHostMiddleware>();
app.UseMiddleware<TenantMiddleware>();
if (!app.Environment.IsDevelopment())
{
    // Developer pages (/dev/*, the component gallery) exist only in Development; elsewhere they are a 404 for everyone,
    // signed in or not, before authentication can turn them into a sign-in challenge.
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/dev", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await next(context);
    });
}

app.UseAuthentication();
// The acting user (app.user_id) of every connection from here on: the authenticated principal's sub.
app.UseMiddleware<ActingUserMiddleware>();
app.UseRequestLocalization();
// A signed-in vendor who opens the tenant's home goes to the vendor home instead of the staff home's 403.
app.UseMiddleware<VendorHomeRedirectMiddleware>();
app.UseAuthorization();
app.UseMiddleware<PlatformAdminEverywhereMiddleware>();
app.UseMiddleware<VendorContextMiddleware>();
app.UseAntiforgery();
app.MapStaticAssets().AllowAnonymous();
app.MapHealthChecks("/health").AllowAnonymous();
app.MapCultureEndpoints();
app.MapSignOutEndpoints();
app.MapVendorRegistrationEndpoints();
app.MapBrandingEndpoints();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapJobsDashboard();

app.Run();

public partial class Program;
