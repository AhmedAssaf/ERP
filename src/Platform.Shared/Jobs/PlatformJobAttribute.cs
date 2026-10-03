namespace Platform.Shared.Jobs;

/// <summary>
/// Marks a class whose public methods the worker may run as Hangfire jobs (W-36 fix round 1, <see cref="JobAllowList"/>).
/// Anything else a job row names (a framework type such as <c>System.Diagnostics.Process</c>, a Hangfire type, an unmarked
/// platform class, a method inherited from <see cref="object"/>) is refused without being invoked. A job class runs without
/// a tenant unless <see cref="TenantScoped"/> is set: a row of an unscoped job that carries a tenant is refused too.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class PlatformJobAttribute : Attribute
{
    /// <summary>True when the job runs as the tenant that enqueued it (the <c>TenantId</c> parameter, <see cref="TenantJobFilter"/>).</summary>
    public bool TenantScoped { get; init; }
}
