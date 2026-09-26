using Microsoft.AspNetCore.Http;
using Platform.Modules.Tenancy.Contracts;
using Platform.Shared.Tenancy;
using Platform.Web.Tenancy;

namespace Platform.IntegrationTests.Web;

public class TenantMiddlewareTests
{
    [Fact]
    public async Task Missing_host_is_404_without_a_lookup()
    {
        var nextCalled = false;
        var middleware = new TenantMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Path = "/";
        var accessor = new TenantAccessor();

        await middleware.InvokeAsync(context, new ThrowingDirectory(), accessor);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
        nextCalled.ShouldBeFalse();
        accessor.Current.ShouldBeNull();
    }

    private sealed class ThrowingDirectory : ITenantDirectory
    {
        public Task<TenantContext?> FindByHostAsync(string host, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The directory must not be asked about an empty host.");

        public void Invalidate(string host)
        {
        }
    }
}
