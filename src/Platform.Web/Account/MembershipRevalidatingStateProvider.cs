using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Vendors.Contracts;
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
/// <item>the circuit holds a vendor context and its user is no longer a user of that company (W-33: an upheld dispute
/// moved the company to its claimant);</item>
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
    private readonly IVendorAccessor _vendors;
    private readonly IVendorUsers _vendorUsers;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _interval;

    public MembershipRevalidatingStateProvider(
        ILoggerFactory loggerFactory,
        IMembershipRevalidation revalidation,
        CircuitSessionGuard guard,
        ITenantAccessor tenants,
        IPlatformRequestContext platform,
        IVendorAccessor vendors,
        IVendorUsers vendorUsers,
        TimeProvider clock)
        : this(loggerFactory, revalidation, guard, tenants, platform, vendors, vendorUsers, clock, Interval)
    {
    }

    internal MembershipRevalidatingStateProvider(
        ILoggerFactory loggerFactory,
        IMembershipRevalidation revalidation,
        CircuitSessionGuard guard,
        ITenantAccessor tenants,
        IPlatformRequestContext platform,
        IVendorAccessor vendors,
        IVendorUsers vendorUsers,
        TimeProvider clock,
        TimeSpan interval)
        : base(loggerFactory)
    {
        _revalidation = revalidation;
        _guard = guard;
        _tenants = tenants;
        _platform = platform;
        _vendors = vendors;
        _vendorUsers = vendorUsers;
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
                && await _revalidation.IsStillMemberAsync(authenticationState.User, signedInAt: null, cancellationToken)
                && await IsStillOfCompanyAsync(authenticationState.User, cancellationToken);
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

    /// <summary>
    /// W-33: a circuit's vendor context is set once, when it opens. An upheld dispute moves the company to its claimant and
    /// deletes the other vendor users' rows; if Keycloak did not also take the removed user out of the organization, the
    /// membership check above still passes, so the circuit also asks the database whose company it is now and ends when
    /// the answer is no longer the company it holds. A circuit without a vendor context asks nothing.
    /// </summary>
    private async Task<bool> IsStillOfCompanyAsync(System.Security.Claims.ClaimsPrincipal user, CancellationToken cancellationToken) =>
        _vendors.Current is not { } vendor
        || (user.FindFirst(IdentityClaims.Subject)?.Value is { Length: > 0 } userId
            && await _vendorUsers.FindCurrentCompanyAsync(userId, cancellationToken) == vendor.CompanyId);
}
