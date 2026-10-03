using Hangfire.Common;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Jobs;

namespace Platform.UnitTests.Shared;

/// <summary>
/// W-36 fix round 2: the job allow-list's type resolver is in place as soon as a job server is registered, before the host
/// is built, so the worker's recurring-job scheduling (which runs after the build and before the host starts) and every
/// stored row it reads go through it.
/// </summary>
public sealed class JobServerRegistrationTests
{
    [Fact]
    public void Registering_a_job_server_sets_the_allow_list_type_resolver_before_anything_is_built()
    {
        var services = new ServiceCollection();

        services.AddJobServer("Host=localhost;Database=none;Username=erp_worker;Password=unused", JobSigningKeys.FromBytes(new byte[32]));

        TypeHelper.CurrentTypeResolver.ShouldBe(JobAllowList.ResolveType);
        Should.Throw<JobRefusedException>(() => TypeHelper.CurrentTypeResolver("System.Diagnostics.Process, System.Diagnostics.Process"));
    }
}
