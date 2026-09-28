using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Platform.Shared.Tenancy;

namespace Platform.Web.Account;

/// <summary>
/// The tenant scheme asks Keycloak for the host tenant's organization only (<c>organization:acme</c> on
/// <c>acme.localhost</c>), never the bare <c>organization</c> scope. With the bare scope Keycloak shows a user who belongs
/// to several organizations (a vendor working with more than one tenant, ADR-0008) a picker listing every tenant they
/// belong to on each sign-in, and the token carries whichever one they picked. The named scope needs no picker, puts
/// this tenant's alias in the token only when the user is a member, and never tells a tenant host about the others.
/// A challenge with no tenant (no tenant page can succeed there) keeps the configured scopes unchanged.
/// </summary>
internal static class TenantOrganizationScope
{
    public const string Scope = "organization";

    public static Task Apply(RedirectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var tenant = context.HttpContext.RequestServices.GetService<ITenantAccessor>()?.Current;
        if (tenant is null || string.IsNullOrWhiteSpace(context.ProtocolMessage.Scope))
        {
            return Task.CompletedTask;
        }

        var scopes = context.ProtocolMessage.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => string.Equals(s, Scope, StringComparison.Ordinal) ? $"{Scope}:{tenant.KeycloakOrgAlias}" : s);
        context.ProtocolMessage.Scope = string.Join(' ', scopes);
        return Task.CompletedTask;
    }
}
