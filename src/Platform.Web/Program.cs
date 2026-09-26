using Platform.Modules.Audit;
using Platform.Modules.Identity;
using Platform.Modules.Tenancy;
using Platform.Modules.Workflow;
using Platform.Shared.Tenancy;
using Platform.Web.Components;

var builder = WebApplication.CreateBuilder(args);
var platformDb = builder.Configuration.GetConnectionString("Platform");
if (string.IsNullOrWhiteSpace(platformDb))
{
    throw new InvalidOperationException("Connection string 'Platform' is not configured. Set it with dotnet user-secrets (see README).");
}

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<TenantAccessor>();
builder.Services.AddScoped<ITenantAccessor>(sp => sp.GetRequiredService<TenantAccessor>());
builder.Services.AddAuditModule(platformDb);
builder.Services.AddTenancyModule(platformDb);
builder.Services.AddIdentityModule();
builder.Services.AddWorkflowModule(platformDb);

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler();
    app.UseHsts();
}

app.UseAntiforgery();
app.MapStaticAssets();
app.MapHealthChecks("/health");
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

public partial class Program;
