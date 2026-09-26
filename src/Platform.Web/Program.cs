using Microsoft.AspNetCore.Components.Server.Circuits;
using Platform.Modules.Audit;
using Platform.Modules.Identity;
using Platform.Modules.Tenancy;
using Platform.Modules.Workflow;
using Platform.Shared;
using Platform.Web.Components;
using Platform.Web.Tenancy;

var builder = WebApplication.CreateBuilder(args);
var platformDb = builder.Configuration.GetConnectionString("Platform");
if (string.IsNullOrWhiteSpace(platformDb))
{
    throw new InvalidOperationException("Connection string 'Platform' is not configured. Set it with dotnet user-secrets (see README).");
}

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddPlatformShared();
builder.Services.AddAuditModule(platformDb);
builder.Services.AddTenancyModule(platformDb);
builder.Services.AddIdentityModule();
builder.Services.AddWorkflowModule(platformDb);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CircuitHandler, TenantCircuitHandler>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler();
    app.UseHsts();
}

app.UseMiddleware<TenantMiddleware>();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapHealthChecks("/health");
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

public partial class Program;
