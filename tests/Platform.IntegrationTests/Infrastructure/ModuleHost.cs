using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Modules.Audit;
using Platform.Modules.Identity;
using Platform.Modules.Operations;
using Platform.Modules.Tenancy;
using Platform.Modules.Workflow;
using Platform.Shared;
using Platform.Shared.Tenancy;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>
/// The modules wired as the web host wires them, without HTTP. One scope stands for one request. With
/// <paramref name="keycloakAdmin"/> (settings <c>KeycloakAdmin:*</c>) the Keycloak Admin API client and the staff service
/// are wired too, as the web host wires them. With <paramref name="clock"/> every module reads that time instead of the system's.
/// With <paramref name="objectStorage"/> (settings <c>ObjectStorage:*</c>) object storage is configured, as the web host does.
/// <paramref name="configure"/> runs last, for a test's own doubles and interceptors.
/// </summary>
internal sealed class ModuleHost : IAsyncDisposable
{
    private readonly ServiceProvider _root;

    public ModuleHost(
        string appConnectionString,
        IConfiguration? keycloakAdmin = null,
        TimeProvider? clock = null,
        IConfiguration? objectStorage = null,
        Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (clock is not null)
        {
            services.AddSingleton(clock);
        }

        services.AddPlatformShared();
        services.AddAuditModule(appConnectionString);
        services.AddTenancyModule(appConnectionString);
        services.AddIdentityModule(appConnectionString);
        services.AddWorkflowModule(appConnectionString);
        services.AddOperationsModule(appConnectionString);
        if (keycloakAdmin is not null)
        {
            services.AddKeycloakAdmin(keycloakAdmin);
        }

        if (objectStorage is not null)
        {
            services.AddObjectStorage(objectStorage);
        }

        configure?.Invoke(services);

        _root = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public IServiceProvider Services => _root;

    public AsyncServiceScope ScopeFor(TenantContext? tenant)
    {
        var scope = _root.CreateAsyncScope();
        if (tenant is not null)
        {
            scope.ServiceProvider.GetRequiredService<TenantAccessor>().Set(tenant);
        }

        return scope;
    }

    /// <summary>A scope marked as the platform console's, as the web host marks a request on the platform host.</summary>
    public AsyncServiceScope PlatformScope()
    {
        var scope = _root.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<PlatformRequestContext>().MarkPlatform();
        return scope;
    }

    public ValueTask DisposeAsync() => _root.DisposeAsync();
}
