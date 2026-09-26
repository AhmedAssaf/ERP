namespace Platform.Shared.Tenancy;

/// <summary>
/// The user a request or circuit acts as: the authenticated principal's Keycloak <c>sub</c>. The connection interceptor
/// writes it to <c>app.user_id</c>, which <c>platform.current_user_id()</c> reads, so security-definer functions take
/// the acting user from the session instead of a parameter a caller could choose.
/// </summary>
public interface IActingUserAccessor
{
    string? UserId { get; }
}

/// <summary>
/// Scoped holder set once per request or circuit, like <see cref="TenantAccessor"/>. Only host infrastructure (the
/// acting-user middleware after authentication, the circuit handler, tests) may call <see cref="Set"/>; application code
/// depends on <see cref="IActingUserAccessor"/> and only reads <see cref="UserId"/>.
/// </summary>
public sealed class ActingUserAccessor : IActingUserAccessor
{
    public string? UserId { get; private set; }

    /// <summary>Sets the user once. Setting the same user again is a no-op; setting a different one throws.</summary>
    public void Set(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        if (UserId is not null && !string.Equals(UserId, userId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The acting user is already set; it cannot be changed within the same request or circuit.");
        }

        UserId = userId;
    }
}
