using System.Diagnostics;
using Hangfire.Client;
using Platform.Shared.Tenancy;

namespace Platform.Shared.Jobs;

/// <summary>
/// Client filter bound to one DI scope: stamps the scope's tenant on every job it creates. <c>TenantId</c> is the
/// searchable parameter (the platform console matches failed jobs on it); <c>Tenant</c> is the context snapshot the
/// worker restores, so a job needs no tenant lookup before its first query. No tenant, no parameters.
/// <c>TraceParent</c> (W-10, spec 5.2) is the current activity's W3C id when the job is created during a request or another
/// job, so <see cref="JobTelemetryFilter"/> runs the job as a child of that span; nothing is stamped without one.
/// </summary>
public sealed class TenantJobFilter(ITenantAccessor tenants) : IClientFilter
{
    public const string TenantIdParameter = "TenantId";
    public const string TenantParameter = "Tenant";
    public const string TraceParentParameter = "TraceParent";

    public void OnCreating(CreatingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (tenants.Current is { } tenant)
        {
            context.SetJobParameter(TenantIdParameter, tenant.TenantId);
            context.SetJobParameter(TenantParameter, tenant);
        }

        if (Activity.Current is { IdFormat: ActivityIdFormat.W3C, Id: { } traceParent })
        {
            context.SetJobParameter(TraceParentParameter, traceParent);
        }
    }

    public void OnCreated(CreatedContext context)
    {
    }
}
