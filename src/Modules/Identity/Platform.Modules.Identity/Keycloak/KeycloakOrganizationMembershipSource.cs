using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Platform.Modules.Identity.Keycloak;

/// <summary>
/// Membership and account answers from the Keycloak Admin API (W-21, <see cref="KeycloakAdminClient.MembershipAsync"/>,
/// <see cref="KeycloakAdminClient.AccountAsync"/>), behind
/// <see cref="MembershipRevalidator"/>. <see cref="OrganizationMembership.Unavailable"/>, never a guess, when the client is
/// not configured or Keycloak does not answer; the failure is logged by type only (N-10). A singleton, so it resolves the
/// typed client per call rather than holding one.
/// </summary>
internal sealed partial class KeycloakOrganizationMembershipSource(
    IServiceProvider services, IOptions<KeycloakAdminOptions> options, ILogger<KeycloakOrganizationMembershipSource> logger)
    : IOrganizationMembershipSource
{
    private int _notConfiguredLogged;

    public Task<OrganizationMembership> CheckAsync(string organizationAlias, string userId, CancellationToken cancellationToken) =>
        AskAsync(organizationAlias, client => client.MembershipAsync(organizationAlias, userId, cancellationToken));

    public Task<OrganizationMembership> CheckAccountAsync(string userId, CancellationToken cancellationToken) =>
        AskAsync("(account)", client => client.AccountAsync(userId, cancellationToken));

    private async Task<OrganizationMembership> AskAsync(string what, Func<KeycloakAdminClient, Task<OrganizationMembership>> ask)
    {
        if (!options.Value.IsConfigured)
        {
            if (Interlocked.Exchange(ref _notConfiguredLogged, 1) == 0)
            {
                NotConfigured(logger);
            }

            return OrganizationMembership.Unavailable;
        }

        try
        {
            return await ask(services.GetRequiredService<KeycloakAdminClient>());
        }
        catch (Exception ex) when (ex is KeycloakAdminException or HttpRequestException or TaskCanceledException
            or JsonException or NotSupportedException)
        {
            // A timeout is the caller's own token or the HttpClient's; a body that is not the expected JSON (a proxy's error
            // page served with 200, say) is a Keycloak that did not answer either. Never ex.Message (N-10): an
            // HttpRequestException can carry the target URL.
            CheckFailed(logger, what, ex.GetType().Name);
            return OrganizationMembership.Unavailable;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Keycloak did not answer the membership check in {Scope} ({ErrorType}).")]
    private static partial void CheckFailed(ILogger logger, string scope, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The Keycloak Admin API is not configured (KeycloakAdmin:*); organization membership cannot be revalidated, so sessions last only the grace period after sign-in.")]
    private static partial void NotConfigured(ILogger logger);
}
