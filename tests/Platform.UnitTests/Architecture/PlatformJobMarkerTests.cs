using System.Reflection;
using Platform.Modules.Operations;
using Platform.Modules.Tenancy;
using Platform.Modules.Vendors;
using Platform.Shared.Jobs;

namespace Platform.UnitTests.Architecture;

/// <summary>
/// W-36 fix round 1: the worker runs only classes marked <see cref="PlatformJobAttribute"/>, so every recurring job the
/// modules schedule must carry it, or the worker would refuse it. A source-free check over the modules' job classes.
/// </summary>
public sealed class PlatformJobMarkerTests
{
    public static TheoryData<string> JobClasses => new(
        "Platform.Modules.Operations.Health.HealthCheckJob",
        "Platform.Modules.Operations.Usage.UsageMetricsJob",
        "Platform.Modules.Tenancy.Branding.BrandingLogoCleanupJob",
        "Platform.Modules.Vendors.Documents.VendorDocumentRescanJob",
        "Platform.Modules.Vendors.Documents.VendorUploadCleanupJob",
        "Platform.Modules.Vendors.Ownership.CrDisputeAlertJob");

    [Theory]
    [MemberData(nameof(JobClasses))]
    public void Every_scheduled_job_class_is_a_platform_job_without_a_tenant(string name)
    {
        var type = Modules().Select(a => a.GetType(name)).SingleOrDefault(t => t is not null);

        type.ShouldNotBeNull(name);
        var marker = type.GetCustomAttribute<PlatformJobAttribute>(inherit: false);
        marker.ShouldNotBeNull(name);
        marker.TenantScoped.ShouldBeFalse(name);
        JobAllowList.IsAllowedType(type).ShouldBeTrue(name);
    }

    [Fact]
    public void No_class_named_like_a_job_in_the_modules_lacks_the_marker()
    {
        var unmarked = Modules()
            .SelectMany(a => a.GetTypes())
            .Where(t => t.IsClass && !t.IsNested && t.Name.EndsWith("Job", StringComparison.Ordinal))
            .Where(t => t.GetMethod("RunAsync", BindingFlags.Public | BindingFlags.Instance) is not null)
            .Where(t => t.GetCustomAttribute<PlatformJobAttribute>(inherit: false) is null)
            .Select(t => t.FullName)
            .ToList();

        unmarked.ShouldBeEmpty();
    }

    private static Assembly[] Modules() =>
        [typeof(OperationsModule).Assembly, typeof(TenancyModule).Assembly, typeof(VendorsModule).Assembly];
}
