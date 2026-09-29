using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Platform.Modules.Identity.Keycloak;

/// <summary>
/// Membership answers from the Keycloak Admin API (W-21, <see cref="KeycloakAdminClient.MembershipAsync"/>), behind
/// <see cref="MembershipRevalidator"/>. <see cref="OrganizationMembership.Unavailable"/>, never a guess, when the client is
/// not configured or Keycloak does not answer; the failure is logged by type only (N-10). A singleton, so it resolves the
/// typed client per call rather than holding one.
/// </summary>
internal sealed partial class KeycloakOrganizationMembershipSource(
    IServiceProvider services, IOptions<KeycloakAdminOptions> options, ILogger<KeycloakOrganizationMembershipSource> logger)
    : IOrganizationMembershipSource
{
    private int _notConfiguredLogged;

    public async Task<OrganizationMembership> CheckAsync(string organizationAlias, string userId, CancellationToken cancellationToken)
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
            return await services.GetRequiredService<KeycloakAdminClient>().MembershipAsync(organizationAlias, userId, cancellationToken);
        }
        catch (Exception ex) when (ex is KeycloakAdminException or HttpRequestException || ex is TaskCanceledException)
        {
            // A timeout is the caller's own token or the HttpClient's; either way Keycloak did not answer. Never ex.Message
            // (N-10): an HttpRequestException can carry the target URL.
            CheckFailed(logger, organizationAlias, ex.GetType().Name);
            return OrganizationMembership.Unavailable;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Keycloak did not answer the membership check in organization {Alias} ({ErrorType}).")]
    private static partial void CheckFailed(ILogger logger, string alias, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The Keycloak Admin API is not configured (KeycloakAdmin:*); organization membership cannot be revalidated, so sessions last only the grace period after sign-in.")]
    private static partial void NotConfigured(ILogger logger);
}
