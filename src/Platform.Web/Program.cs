using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
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
using Platform.Shared.Telemetry;
using Platform.UI;
using Platform.Web.Account;
using Platform.Web.Branding;
using Platform.Web.Components;
using Platform.Web.Edge;
using Platform.Web.Localization;
using Platform.Web.PlatformHost;
using Platform.Web.Telemetry;
using Platform.Web.Tenancy;
using Platform.Web.Usage;
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

// W-10 (spec O-3 to O-6, O-16): traces and metrics through OpenTelemetry, logs through Serilog as a logging provider, all
// over OTLP only when Telemetry:OtlpEndpoint is set; outside Development and Testing no console output. Requests to /health,
// /alive, the framework's files and static assets get no span.
builder.AddPlatformTelemetry(TelemetryNames.Services.Web);
builder.AddWebTelemetry();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddPlatformShared();
// One bucket for every module (settings ObjectStorage:*): tenant logos (F-02) and storage usage in the console (F-54).
builder.Services.AddObjectStorage(builder.Configuration);
// F-12: vendor documents are scanned by ClamAV before they are listed (settings ClamAv:*, as the worker's health check).
builder.Services.AddVirusScanner(builder.Configuration);
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
builder.Services.AddVendorPortal(builder.Configuration);
// V-9: the upload API is limited per vendor company (Vendors:UploadRequestsPerMinute).
builder.Services.AddVendorUploadRateLimit();
// W-33: every post of /vendor/dispute counts, per person (DisputeRateLimit).
builder.Services.AddVendorDisputeRateLimit();
builder.Services.AddScoped<VendorContextResolver>();
// The web host only enqueues and reads jobs (D-6): Hangfire storage without a server, plus the dashboard (task 7).
builder.Services.AddJobClient(platformDb);
builder.Services.AddJobsDashboard();
builder.Services.AddOperationsConsole(builder.Configuration);
builder.Services.AddScoped<TenantOverview>();
// W-10 (spec 6.6): the console usage page's figures, from the circuit registry and the usage job's stored result.
builder.Services.AddScoped<UsageOverview>();
builder.Services.AddSingleton<GrafanaLink>();
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
        // Fixed 30-minute lifetime, no sliding: the session length. Membership is revalidated separately (W-21): the
        // sign-in is stamped, and each request checks the host tenant's organization in Keycloak (cached, see
        // MembershipRevalidation), so a removed member is challenged within minutes, not at the cookie's expiry.
        options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
        options.SlidingExpiration = false;
        options.Events.OnSigningIn = MembershipRevalidation.OnSigningIn;
        options.Events.OnValidatePrincipal = MembershipRevalidation.OnValidatePrincipal;
        options.ForwardChallenge = OpenIdConnectDefaults.AuthenticationScheme;
        // A 403, with the access-removed page as its body when the refusal is for a missing organization (W-21, D2).
        options.Events.OnRedirectToAccessDenied = AccessRemovedPage.OnForbidden;
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
        // Narrowed to the host tenant's organization at each challenge (TenantOrganizationScope).
        options.Scope.Add(TenantOrganizationScope.Scope);
        // email and email_verified: a member row seeded by email is bound to the user on first sign-in (F-07 dev seed).
        options.Scope.Add("email");
        options.TokenValidationParameters.NameClaimType = IdentityClaims.Username;
        // W-21: the token's issue time is when Keycloak vouched for the organization; the sign-in stamp uses it.
        options.ClaimActions.Remove(MembershipRevalidation.IssuedAtClaim);
        options.Events.OnRedirectToIdentityProviderForSignOut = SignOutEndpoints.NameClientOnEndSession;
        // W-21, D2: the id token rides in the cookie so sign-out ends the Keycloak session too (id_token_hint).
        options.Events.OnTokenValidated = SignOutEndpoints.KeepIdToken;
        // F-11: /vendor/register goes to the realm's registration endpoint (VendorRegistrationEndpoints).
        options.Events.OnRedirectToIdentityProvider = async context =>
        {
            await TenantOrganizationScope.Apply(context);
            await VendorRegistrationEndpoints.UseRegistrationEndpoint(context);
        };
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
    // The access-removed page (W-21, D2): signed in, whatever the organization.
    options.AddPolicy(AccessRemovedPage.PolicyName, AccessRemovedPage.Policy);
    // F-07: TenantAdmin, ContractsOfficer, TechnicalEvaluator, FinanceApprover (same tenant plus the role in identity.members).
    options.AddTenantRolePolicies();
    // Vendor pages (spec section 3, V-3): the vendor's own requirements plus membership of the host tenant's organization.
    options.AddPolicy(VendorPolicies.Vendor, new AuthorizationPolicyBuilder()
        .Combine(IdentityModule.SameTenantPolicy)
        .Combine(VendorsModule.VendorRequirements)
        .Build());
    options.AddPolicy(VendorPolicies.VendorApplicant, VendorsModule.VendorApplicantPolicy);
    // /vendor/join (V-7): the vendor's own requirements without the host check, for a tenant it does not work with yet.
    options.AddPolicy(VendorPolicies.JoiningVendor, VendorsModule.VendorRequirements);
    // /admin/vendors (V-11): a contracts officer or tenant admin of the host tenant.
    options.AddPolicy(VendorPolicies.VendorManager, IdentityModule.AnyTenantRolePolicy(TenantRoles.TenantAdmin, TenantRoles.ContractsOfficer));
});
builder.Services.AddSingleton<IAuthorizationPolicyProvider, HostAwareAuthorizationPolicyProvider>();
builder.Services.AddCascadingAuthenticationState();
// W-21: open circuits revalidate their user's organization membership every minute; an ended session reloads into the
// sign-in page and runs no further event.
builder.Services.AddScoped<CircuitSessionGuard>();
builder.Services.AddScoped<CircuitHandler>(sp => sp.GetRequiredService<CircuitSessionGuard>());
builder.Services.AddScoped<AuthenticationStateProvider, MembershipRevalidatingStateProvider>();
// W-10 business metrics (spec 6.3): the connected circuits of this instance, counted per tenant slug and kind after the
// session guard, on meter WaslaBid.Usage and for the console usage page.
builder.Services.AddSingleton<ConnectedCircuits>();
builder.Services.AddScoped<CircuitHandler, UsageCircuitHandler>();
// W-10 (O-9): every inbound circuit activity logs with the circuit's tenant, user and vendor company; ordered last.
builder.Services.AddScoped<CircuitHandler, CircuitTelemetryHandler>();
builder.Services.AddPlatformLocalization();
builder.Services.AddPlatformUI();

// W-24 (Platform.Web/Edge): forwarded headers only from Caddy (ForwardedHeaders:*), and one Data Protection key ring in
// PostgreSQL for every instance under its own role (ConnectionStrings:KeyRing), encrypted outside Development (DataProtection:*).
builder.Services.AddEdgeForwardedHeaders(builder.Configuration, builder.Environment);
builder.Services.AddKeyRing(builder.Configuration, builder.Environment);

var app = builder.Build();

// W-10 (O-7): first, so every response carries its trace id in X-Correlation-Id, the 404s and 500s below included.
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler();
    app.UseHsts();
}

app.UseMiddleware<PlatformHostMiddleware>();
app.UseMiddleware<TenantMiddleware>();
// W-10 (O-9): the tenant's id and slug on the server span and on every log record of the request from here on.
app.UseMiddleware<RequestTelemetryMiddleware>();
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

// W-21, D2: a refusal for a missing organization re-executes at the access-removed page, keeping its 403.
app.UseAccessRemovedPage();
app.UseAuthentication();
// The acting user (app.user_id) of every connection from here on: the authenticated principal's sub.
app.UseMiddleware<ActingUserMiddleware>();
// W-10 (O-9): the acting user's sub as user.id, on the span and the records from here on.
app.UseUserTelemetry();
app.UseRequestLocalization();
// A signed-in vendor who opens the tenant's home goes to the vendor home instead of the staff home's 403.
app.UseMiddleware<VendorHomeRedirectMiddleware>();
app.UseAuthorization();
app.UseMiddleware<PlatformAdminEverywhereMiddleware>();
app.UseMiddleware<VendorContextMiddleware>();
// W-10 (O-9): the vendor company of a vendor request, on the span and the records from here on.
app.UseVendorCompanyTelemetry();
// W-10 (spec 6.4): after the vendor context, an authorized staff or vendor request marks its user active for the hour.
app.UseMiddleware<UserActivityMiddleware>();
app.UseAntiforgery();
// After the vendor context: the upload API's limit is partitioned by the vendor company.
app.UseRateLimiter();
app.MapStaticAssets().AllowAnonymous();
app.MapHealthChecks("/health").AllowAnonymous();
// W-10 (O-15): liveness for container health checks. No check runs: the process answering is the whole answer.
app.MapHealthChecks("/alive", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapCultureEndpoints();
app.MapSignOutEndpoints();
app.MapVendorRegistrationEndpoints();
app.MapVendorUploadEndpoints();
app.MapBrandingEndpoints();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapJobsDashboard();
// W-10: GET /dev/throw, Development only, for the end-to-end checks of plan task 10.
app.MapDeliberateFailure(app.Environment);

app.Run();

public partial class Program;
