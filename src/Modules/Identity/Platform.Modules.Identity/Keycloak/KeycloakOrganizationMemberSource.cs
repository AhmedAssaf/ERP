using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Platform.Modules.Identity.Keycloak;

/// <summary>
/// Member counts from the Keycloak Admin API, behind <see cref="CachingOrganizationMembers"/> (F-54 as narrowed). Null,
/// never a guess, when the client is not configured, the organization does not exist, or Keycloak does not answer; the
/// failure is logged. A singleton, so it resolves the typed client per call rather than holding one.
/// </summary>
internal sealed partial class KeycloakOrganizationMemberSource(
    IServiceProvider services, IOptions<KeycloakAdminOptions> options, ILogger<KeycloakOrganizationMemberSource> logger)
    : IOrganizationMemberSource
{
    public async Task<int?> CountAsync(string organizationAlias, CancellationToken cancellationToken = default)
    {
        if (!options.Value.IsConfigured)
        {
            return null;
        }

        try
        {
            return await services.GetRequiredService<KeycloakAdminClient>().CountOrganizationMembersAsync(organizationAlias, cancellationToken);
        }
        catch (Exception ex) when (ex is KeycloakAdminException or HttpRequestException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Never ex.Message (N-10): an HttpRequestException can carry the target URL.
            CountFailed(logger, organizationAlias, ex.GetType().Name);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Keycloak did not give the member count of organization {Alias} ({ErrorType}).")]
    private static partial void CountFailed(ILogger logger, string alias, string errorType);
}
