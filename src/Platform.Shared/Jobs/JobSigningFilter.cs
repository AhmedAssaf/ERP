using Hangfire.Client;
using Hangfire.Common;

namespace Platform.Shared.Jobs;

/// <summary>
/// W-42: signs every job the platform creates (<see cref="JobAuthenticity"/>), in both hosts: the web host's and the worker's
/// <c>IBackgroundJobClient</c>, the recurring job manager, and the worker's recurring scheduler (the job server's own
/// factory). Ordered after every other client filter, so the signature covers the tenant <see cref="TenantJobFilter"/>
/// stamped and the recurring job id Hangfire set. A row written any other way, such as SQL injected as the application
/// role, has no valid signature and is refused by the worker.
/// </summary>
internal sealed class JobSigningFilter(JobAuthenticity authenticity) : IClientFilter, IJobFilter
{
    public bool AllowMultiple => false;

    public int Order => int.MaxValue;

    public void OnCreating(CreatingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var binding = JobBinding.FromParameters(context.Parameters);
        context.SetJobParameter(JobAuthenticity.ParameterName, authenticity.Sign(context.Job, binding));
    }

    public void OnCreated(CreatedContext context)
    {
    }
}
