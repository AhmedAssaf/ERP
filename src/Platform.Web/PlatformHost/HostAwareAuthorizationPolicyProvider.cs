using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Platform.Web.PlatformHost;

/// <summary>
/// Picks the default and fallback policy by host (spec 3.1): PlatformAdmin for a platform request, the configured
/// SameTenant policies otherwise. A console endpoint that forgets its policy is therefore still platform-only, and the
/// tenant policies never evaluate a platform request. Named policies come from the configured options unchanged.
/// Policies are not cached per endpoint, since the answer depends on the request. Inside a Blazor circuit there is no
/// request, so components must name their policy explicitly.
/// </summary>
internal sealed class HostAwareAuthorizationPolicyProvider(IOptions<AuthorizationOptions> options, IHttpContextAccessor httpContextAccessor)
    : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _configured = new(options);

    public bool AllowsCachingPolicies => false;

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() =>
        IsPlatformRequest ? Task.FromResult(PlatformAuthentication.AdminPolicy) : _configured.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() =>
        IsPlatformRequest ? Task.FromResult<AuthorizationPolicy?>(PlatformAuthentication.AdminPolicy) : _configured.GetFallbackPolicyAsync();

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName) => _configured.GetPolicyAsync(policyName);

    private bool IsPlatformRequest => httpContextAccessor.HttpContext is { } context && PlatformRequest.IsPlatform(context);
}
