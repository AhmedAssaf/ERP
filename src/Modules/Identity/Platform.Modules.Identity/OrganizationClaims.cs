using System.Security.Claims;
using System.Text.Json;
using Platform.Modules.Identity.Contracts;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Identity;

/// <summary>
/// Reads Keycloak organization membership. The built-in "organization" scope emits ["acme"], which arrives as one claim
/// per alias; with "add organization id" it emits {"acme":{"id":"..."}}. Tenants match on the alias because Keycloak
/// generates organization ids on import (spec section 7).
/// </summary>
internal static class OrganizationClaims
{
    public static IReadOnlySet<string> Aliases(ClaimsPrincipal principal)
    {
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var claim in principal.FindAll(IdentityClaims.Organization))
        {
            var value = claim.Value.Trim();
            if (!value.StartsWith('{') && !value.StartsWith('['))
            {
                aliases.Add(value);
                continue;
            }

            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    aliases.Add(property.Name);
                }
            }
            else
            {
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    aliases.Add(item.GetString() ?? string.Empty);
                }
            }
        }

        aliases.Remove(string.Empty);
        return aliases;
    }

    public static bool BelongsTo(ClaimsPrincipal principal, TenantContext tenant) =>
        Aliases(principal).Contains(tenant.KeycloakOrgAlias);
}
