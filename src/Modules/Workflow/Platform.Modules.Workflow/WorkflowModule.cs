using Microsoft.Extensions.DependencyInjection;

namespace Platform.Modules.Workflow;

public static class WorkflowModule
{
    public static IServiceCollection AddWorkflowModule(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddSingleton<WorkflowModuleMarker>();
        return services;
    }
}

internal sealed class WorkflowModuleMarker;
