using Hangfire.Common;
using Platform.Shared.Jobs;

namespace Platform.UnitTests.Shared;

/// <summary>
/// W-36 fix round 1: the worker's Hangfire type resolver and job check. Types resolve only from the platform's and
/// Hangfire's assemblies and a short list of framework argument types; a job runs only when its class carries
/// <see cref="PlatformJobAttribute"/>, its method is the class's own public method, and an unscoped job carries no tenant.
/// </summary>
public sealed class JobAllowListTests
{
    [Theory]
    [InlineData("System.Diagnostics.Process, System.Diagnostics.Process")]
    [InlineData("System.Environment, System.Private.CoreLib")]
    [InlineData("System.IO.FileInfo, System.Private.CoreLib")]
    [InlineData("System.Object, mscorlib")]
    [InlineData("System.Collections.Generic.List`1[[System.IO.FileInfo, System.Private.CoreLib]], System.Private.CoreLib")]
    [InlineData("System.IO.FileInfo[], System.Private.CoreLib")]
    public void The_resolver_refuses_types_outside_the_allow_list(string typeName)
    {
        Should.Throw<JobRefusedException>(() => JobAllowList.ResolveType(typeName));
    }

    [Theory]
    [InlineData("System.Threading.CancellationToken, mscorlib")]
    [InlineData("System.Nullable`1[[System.Guid, mscorlib]], mscorlib")]
    [InlineData("System.String, System.Private.CoreLib")]
    [InlineData("System.Collections.Generic.IReadOnlyList`1[[System.Int64, mscorlib]], mscorlib")]
    [InlineData("Hangfire.States.EnqueuedState, Hangfire.Core")]
    [InlineData("Platform.Shared.Tenancy.TenantContext, Platform.Shared")]
    public void The_resolver_resolves_platform_hangfire_and_argument_types(string typeName)
    {
        JobAllowList.ResolveType(typeName).ShouldNotBeNull();
    }

    [Fact]
    public void A_marked_platform_job_runs()
    {
        JobAllowList.Refusal(Job.FromExpression<MarkedJob>(j => j.Run(1)), null).ShouldBeNull();
    }

    [Fact]
    public void An_unmarked_class_a_framework_method_and_an_inherited_method_are_refused()
    {
        JobAllowList.Refusal(Job.FromExpression<UnmarkedJob>(j => j.Run()), null).ShouldNotBeNull();
        JobAllowList.Refusal(Job.FromExpression(() => Console.WriteLine("x")), null).ShouldNotBeNull();
        JobAllowList.Refusal(Job.FromExpression<MarkedJob>(j => j.ToString()), null).ShouldNotBeNull();
        JobAllowList.Refusal((Job?)null, null).ShouldNotBeNull();
    }

    [Fact]
    public void A_tenant_is_refused_for_an_unscoped_job_and_accepted_for_a_scoped_one()
    {
        const string tenant = "\"3f1d2c6e-0000-4000-8000-000000000001\"";
        JobAllowList.Refusal(Job.FromExpression<MarkedJob>(j => j.Run(1)), tenant).ShouldNotBeNull();
        JobAllowList.Refusal(Job.FromExpression<ScopedJob>(j => j.Run()), tenant).ShouldBeNull();
    }

#pragma warning disable CA1822 // Hangfire jobs are instance methods of their class, as the platform's are.
    [PlatformJob]
    public sealed class MarkedJob
    {
        public int Run(int value) => value;
    }

    public sealed class UnmarkedJob
    {
        public void Run()
        {
        }
    }

    [PlatformJob(TenantScoped = true)]
    public sealed class ScopedJob
    {
        public void Run()
        {
        }
    }
#pragma warning restore CA1822
}
