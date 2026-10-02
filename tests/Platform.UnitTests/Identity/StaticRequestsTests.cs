using Microsoft.AspNetCore.Http;
using Platform.Modules.Identity.Members;

namespace Platform.UnitTests.Identity;

/// <summary>The members claims transformation skips the probes (W-10, O-15): only the exact paths, never a path below them.</summary>
public sealed class StaticRequestsTests
{
    [Theory]
    [InlineData("/health")]
    [InlineData("/alive")]
    [InlineData("/ALIVE")]
    public void The_exact_probe_paths_need_no_tenant_roles(string path) =>
        StaticRequests.IsStatic(new PathString(path)).ShouldBeTrue();

    [Theory]
    [InlineData("/health/x")]
    [InlineData("/alive/x")]
    [InlineData("/alivex")]
    public void A_path_under_a_probe_is_an_ordinary_request(string path) =>
        StaticRequests.IsStatic(new PathString(path)).ShouldBeFalse();
}
