using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace Platform.Web.PlatformHost;

/// <summary>
/// PlatformAdmin on every platform request (D-2), after <c>UseAuthorization</c>. The host-aware provider makes
/// PlatformAdmin the default and fallback policy on the platform host, but an endpoint that names something else
/// (<c>[Authorize(Roles = "platform-admin")]</c>, another named policy) would otherwise be satisfied without the OTP
/// step. Here every platform endpoint not marked anonymous (health, static assets, the culture switch) must also pass
/// PlatformAdmin: a request without a platform session is challenged, a session that fails the policy gets 403.
/// The sign-in callbacks never get here; the OpenID Connect handler answers them in <c>UseAuthentication</c>.
/// </summary>
internal sealed class PlatformAdminEverywhereMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IPolicyEvaluator evaluator, IAuthorizationMiddlewareResultHandler results)
    {
        if (!PlatformRequest.IsPlatform(context) || context.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            await next(context);
            return;
        }

        var policy = PlatformAuthentication.AdminPolicy;
        var authentication = await evaluator.AuthenticateAsync(policy, context);
        var authorization = await evaluator.AuthorizeAsync(policy, authentication, context, context);
        await results.HandleAsync(next, context, policy, authorization);
    }
}
