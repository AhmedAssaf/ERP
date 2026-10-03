using System.Reflection;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Tenancy;

namespace Platform.Shared.Jobs;

/// <summary>
/// Gives every job its own DI scope and, when the job carries a tenant, sets it once on the scope's
/// <see cref="TenantAccessor"/> before the job is resolved, so EF Core's connection interceptor applies RLS for it.
/// A tenant snapshot that disagrees with the <c>TenantId</c> parameter fails the job instead of guessing.
/// </summary>
public sealed class TenantJobActivator(IServiceScopeFactory scopes) : JobActivator
{
    public override JobActivatorScope BeginScope(JobActivatorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var tenant = context.GetJobParameter<TenantContext?>(TenantJobFilter.TenantParameter);
        var tenantId = context.GetJobParameter<Guid?>(TenantJobFilter.TenantIdParameter);
        if (tenant?.TenantId != tenantId)
        {
            throw new InvalidOperationException(
                $"Job {context.BackgroundJob.Id} carries inconsistent tenant parameters; it is not run.");
        }

        // W-36 fix round 2: the job allow-list's tenant rule again, on the values read here and used below, so a row whose
        // parameters change after JobAllowListFilter checked them cannot put a job that runs without a tenant in one.
        if (tenant is not null && context.BackgroundJob.Job?.Type.GetCustomAttribute<PlatformJobAttribute>(inherit: false) is not { TenantScoped: true })
        {
            throw new JobRefusedException(
                $"Job {context.BackgroundJob.Id} carries a tenant, but {context.BackgroundJob.Job?.Type.FullName} runs without one; it is not run.");
        }

        var scope = scopes.CreateAsyncScope();
        if (tenant is not null)
        {
            scope.ServiceProvider.GetRequiredService<TenantAccessor>().Set(tenant);
        }

        return new Scope(scope);
    }

    private sealed class Scope(AsyncServiceScope scope) : JobActivatorScope
    {
        public override object Resolve(Type type) => ActivatorUtilities.GetServiceOrCreateInstance(scope.ServiceProvider, type);

        public override void DisposeScope() => scope.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
