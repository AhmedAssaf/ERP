using Hangfire.Dashboard;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace Platform.Web.PlatformHost;

/// <summary>
/// The Hangfire dashboard at <c>/platform/jobs</c> (plan task 7) answers only a platform admin: the request must be a
/// platform request (<see cref="PlatformHostMiddleware"/> already returns 404 elsewhere) and the PlatformAdmin policy
/// must pass against the platform cookie. The endpoint also requires the policy; this filter holds on its own, so the
/// dashboard stays closed if the endpoint's metadata is ever lost. Hangfire answers 401 or 403 when it refuses.
/// </summary>
internal sealed class JobsDashboardAuthorizationFilter : IDashboardAsyncAuthorizationFilter
{
    public Task<bool> AuthorizeAsync(DashboardContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return IsPlatformAdminAsync(context.GetHttpContext());
    }

    internal static async Task<bool> IsPlatformAdminAsync(HttpContext context)
    {
        if (!PlatformRequest.IsPlatform(context))
        {
            return false;
        }

        var services = context.RequestServices;
        var policy = await services.GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync(PlatformAuthentication.PolicyName)
            ?? throw new InvalidOperationException($"The {PlatformAuthentication.PolicyName} policy is not registered.");
        var evaluator = services.GetRequiredService<IPolicyEvaluator>();
        var authentication = await evaluator.AuthenticateAsync(policy, context);
        var authorization = await evaluator.AuthorizeAsync(policy, authentication, context, resource: null);
        return authorization.Succeeded;
    }
}
