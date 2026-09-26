using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.Modules.Workflow.Contracts;
using Platform.Modules.Workflow.Persistence;
using Platform.Shared.Data;

namespace Platform.Modules.Workflow;

public static class WorkflowModule
{
    public static IServiceCollection AddWorkflowModule(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddModuleDbContext<WorkflowDbContext>(connectionString);
        services.AddScoped<IWorkflowDefinitions, WorkflowDefinitions>();
        services.AddScoped<IWorkflowService, WorkflowService>();
        return services;
    }

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlConnection connection, CancellationToken cancellationToken = default) =>
        SqlMigrator.ApplyAsync(connection, "workflow", typeof(WorkflowModule).Assembly, cancellationToken);
}
