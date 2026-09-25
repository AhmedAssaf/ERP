using Elsa.Extensions;
using Elsa.Studio.Contracts;
using Elsa.Studio.Core.BlazorServer.Extensions;
using Elsa.Studio.Extensions;
using Elsa.Studio.Localization.BlazorServer.Extensions;
using Elsa.Studio.Localization.Models;
using Elsa.Studio.Models;
using Elsa.Studio.Shell.Extensions;
using Elsa.Studio.Translations;
using Elsa.Studio.Workflows.Extensions;
using Elsa.Studio.Workflows.Designer.Extensions;
using ElsaStudioSpike;
using ElsaWorkflowSpike;

// W-20 question 3: can Elsa Studio render in Arabic, right-to-left, in a tenant's colour?
// Elsa Server and Studio in one process, security off, in-memory stores. Throwaway.
var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// ---- Elsa Server: the API that Studio talks to, with our three activities in the toolbox ----
Elsa.EndpointSecurityOptions.DisableSecurity();
builder.Services.AddElsa(elsa => elsa
    .UseWorkflowManagement()
    .UseWorkflowRuntime()
    .UseWorkflowsApi()
    .AddActivitiesFrom<ApprovalStep>());

// ---- Elsa Studio ----
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor(o =>
{
    o.RootComponents.RegisterCustomElsaStudioElements();
    o.RootComponents.MaxJSRootComponents = 1000;
});

var backend = new BackendApiConfig { ConfigureBackendOptions = o => cfg.GetSection("Backend").Bind(o) };
builder.Services.AddCore(o => o.Theme = TenantThemeProvider.Id);
builder.Services.AddStudioThemeProvider<TenantThemeProvider>(TenantThemeProvider.Id);
builder.Services.AddScoped<IUnauthorizedComponentProvider, NoLoginProvider>();
builder.Services.AddScoped<Elsa.Studio.Authentication.Abstractions.Contracts.IHttpConnectionOptionsConfigurator, NoAuthConnectionConfigurator>();
builder.Services.AddShell(o => cfg.GetSection("Shell").Bind(o));
builder.Services.AddRemoteBackend(backend);
builder.Services.AddWorkflowsModule();
builder.Services.AddLocalizationModule(new LocalizationConfig
{
    ConfigureLocalizationOptions = o => cfg.GetSection("Localization").Bind(o)
});
builder.Services.AddTranslations();

var app = builder.Build();
app.UseElsaLocalization();
app.UseStaticFiles();
app.UseRouting();
app.UseWorkflowsApi();
app.MapControllers();
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");
app.Run();
