using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Platform.Modules.Identity.Contracts;
using Platform.Shared.Tenancy;

namespace Platform.Web.Account;

/// <summary>
/// W-21: the authentication state provider of every scope (circuits and prerendering), revalidating an open circuit's
/// user every <see cref="Interval"/>. The circuit's session ends (the base class turns its user anonymous and
/// <see cref="CircuitSessionGuard"/> reloads it into the sign-in page) when:
/// <list type="bullet">
/// <item>the cookie of the circuit's connection request has expired (<see cref="CircuitSessionGuard.SessionExpiresAt"/>),
/// so a circuit never outlives the 30-minute (tenant) or 15-minute (platform) cookie;</item>
/// <item>the circuit has no tenant and is not the platform console's: on a tenant host the circuit handler always sets
/// one, so a missing tenant is a fault and fails closed rather than skipping the check;</item>
/// <item><see cref="IMembershipRevalidation"/> says the session no longer stands (the same check as HTTP requests, with
/// no sign-in time: the connection request already recorded it);</item>
/// <item>the check fails with an exception (the base class logs it).</item>
/// </list>
/// A platform circuit has no tenant and no organization to revalidate; it stands until its cookie expires.
/// </summary>
internal sealed class MembershipRevalidatingStateProvider : RevalidatingServerAuthenticationStateProvider
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly IMembershipRevalidation _revalidation;
    private readonly CircuitSessionGuard _guard;
    private readonly ITenantAccessor _tenants;
    private readonly IPlatformRequestContext _platform;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _interval;

    public MembershipRevalidatingStateProvider(
        ILoggerFactory loggerFactory,
        IMembershipRevalidation revalidation,
        CircuitSessionGuard guard,
        ITenantAccessor tenants,
        IPlatformRequestContext platform,
        TimeProvider clock)
        : this(loggerFactory, revalidation, guard, tenants, platform, clock, Interval)
    {
    }

    internal MembershipRevalidatingStateProvider(
        ILoggerFactory loggerFactory,
        IMembershipRevalidation revalidation,
        CircuitSessionGuard guard,
        ITenantAccessor tenants,
        IPlatformRequestContext platform,
        TimeProvider clock,
        TimeSpan interval)
        : base(loggerFactory)
    {
        _revalidation = revalidation;
        _guard = guard;
        _tenants = tenants;
        _platform = platform;
        _clock = clock;
        _interval = interval;
    }

    protected override TimeSpan RevalidationInterval => _interval;

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authenticationState);
        bool valid;
        try
        {
            valid = !(_guard.SessionExpiresAt is { } expires && _clock.GetUtcNow() >= expires)
                && (_tenants.Current is not null || _platform.IsPlatform)
                && await _revalidation.IsStillMemberAsync(authenticationState.User, signedInAt: null, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _guard.End();
            throw;
        }

        if (!valid)
        {
            _guard.End();
        }

        return valid;
    }
}
