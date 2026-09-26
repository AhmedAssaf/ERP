using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Platform.Shared.Tenancy;
using Platform.Web.PlatformHost;
using Platform.Web.Tenancy;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// The acting user of a request (<c>app.user_id</c>, read by <c>platform.current_user_id()</c>): the authenticated
/// principal's own <c>sub</c>, on tenant and platform hosts alike, and nothing for an anonymous request.
/// </summary>
public class ActingUserMiddlewareTests
{
    [Fact]
    public async Task An_authenticated_request_acts_as_its_principals_subject()
    {
        var accessor = new ActingUserAccessor();
        var seen = default(string);
        var middleware = new ActingUserMiddleware(_ =>
        {
            seen = accessor.UserId;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext { User = Principal("user-42") };
        context.Request.Host = new HostString("acme.localhost");

        await middleware.InvokeAsync(context, accessor);

        seen.ShouldBe("user-42", "the acting user is set before the rest of the pipeline runs");
    }

    [Fact]
    public async Task A_platform_request_acts_as_its_principals_subject_too()
    {
        var accessor = new ActingUserAccessor();
        var middleware = new ActingUserMiddleware(_ => Task.CompletedTask);
        var context = new DefaultHttpContext { User = Principal("platform-admin-1") };
        context.Request.Host = new HostString("platform.localhost");
        PlatformRequest.Mark(context);

        await middleware.InvokeAsync(context, accessor);

        accessor.UserId.ShouldBe("platform-admin-1");
    }

    [Fact]
    public async Task An_anonymous_request_or_one_without_a_subject_has_no_acting_user()
    {
        foreach (var user in new[]
        {
            new ClaimsPrincipal(new ClaimsIdentity()),
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "not-authenticated")])),
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("name", "no subject")], "Test")),
        })
        {
            var accessor = new ActingUserAccessor();
            var nextCalled = false;
            var middleware = new ActingUserMiddleware(_ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });

            await middleware.InvokeAsync(new DefaultHttpContext { User = user }, accessor);

            nextCalled.ShouldBeTrue();
            accessor.UserId.ShouldBeNull();
        }
    }

    internal static ClaimsPrincipal Principal(string subject) =>
        new(new ClaimsIdentity([new Claim("sub", subject)], "Test", "sub", "role"));
}
