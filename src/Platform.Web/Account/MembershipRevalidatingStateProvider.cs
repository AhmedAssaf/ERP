using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Platform.Modules.Identity.Contracts;

namespace Platform.Web.Account;

/// <summary>
/// W-21: the authentication state provider of every scope (circuits and prerendering), revalidating an open circuit's
/// user every <see cref="Interval"/> through the same <see cref="IMembershipRevalidation"/> as HTTP requests, with no
/// sign-in time (the connection request already recorded it). When the session no longer stands, the base class turns the
/// circuit's user anonymous and <see cref="CircuitSessionGuard"/> ends the circuit's session. A failing check ends it
/// too (the base class logs the error); on the platform host there is no tenant, so a circuit stands as its cookie does.
/// </summary>
internal sealed class MembershipRevalidatingStateProvider : RevalidatingServerAuthenticationStateProvider
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly IMembershipRevalidation _revalidation;
    private readonly CircuitSessionGuard _guard;
    private readonly TimeSpan _interval;

    public MembershipRevalidatingStateProvider(ILoggerFactory loggerFactory, IMembershipRevalidation revalidation, CircuitSessionGuard guard)
        : this(loggerFactory, revalidation, guard, Interval)
    {
    }

    internal MembershipRevalidatingStateProvider(
        ILoggerFactory loggerFactory, IMembershipRevalidation revalidation, CircuitSessionGuard guard, TimeSpan interval)
        : base(loggerFactory)
    {
        _revalidation = revalidation;
        _guard = guard;
        _interval = interval;
    }

    protected override TimeSpan RevalidationInterval => _interval;

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authenticationState);
        bool valid;
        try
        {
            valid = await _revalidation.IsStillMemberAsync(authenticationState.User, signedInAt: null, cancellationToken);
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
