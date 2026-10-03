using System.Reflection;
using Platform.Modules.Operations;
using Platform.Modules.Tenancy;
using Platform.Modules.Vendors;
using Platform.Shared.Jobs;

namespace Platform.UnitTests.Architecture;

/// <summary>
/// W-36 pentest (2026-10-03): the job allow-list (<see cref="JobAllowList.Refusal(Hangfire.Common.Job?, string?)"/>) lets the
/// worker run <em>any</em> public method of a class marked <see cref="PlatformJobAttribute"/>, not only the method the
/// module schedules. The web role (<c>erp_app</c>) can write Hangfire's tables, so it can enqueue a row naming any such
/// method with any arguments. That is safe only while no <c>[PlatformJob]</c> class exposes a public method that takes
/// attacker-controlled data: a method like <c>DeleteObject(string key)</c> on a marked class would be an enqueue gadget
/// that passes the allow-list. This test freezes the current, safe surface: every public method of a marked job class
/// takes nothing but a <see cref="CancellationToken"/>. Adding a data-taking public method to a job class must be a
/// conscious decision reviewed as a worker gadget, which turning this test red forces.
/// </summary>
public sealed class PlatformJobMethodSurfaceTests
{
    [Fact]
    public void No_platform_job_class_exposes_a_public_method_that_takes_attacker_controlled_arguments()
    {
        var jobClasses = Modules()
            .SelectMany(a => a.GetTypes())
            .Where(t => t.GetCustomAttribute<PlatformJobAttribute>(inherit: false) is not null)
            .ToList();

        jobClasses.ShouldNotBeEmpty("the modules define [PlatformJob] classes to check");

        var offenders = new List<string>();
        foreach (var type in jobClasses)
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName); // not property accessors or operators
            foreach (var method in methods)
            {
                var risky = method.GetParameters().Where(p => p.ParameterType != typeof(CancellationToken)).ToList();
                if (risky.Count > 0)
                {
                    offenders.Add($"{type.FullName}.{method.Name}({string.Join(", ", risky.Select(p => p.ParameterType.Name))})");
                }
            }
        }

        offenders.ShouldBeEmpty(
            "a [PlatformJob] class exposes a public method taking data the web role could forge into a Hangfire row: "
            + string.Join("; ", offenders));
    }

    private static Assembly[] Modules() =>
        [typeof(OperationsModule).Assembly, typeof(TenancyModule).Assembly, typeof(VendorsModule).Assembly];
}
