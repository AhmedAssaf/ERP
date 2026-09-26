using System.Security.Claims;
using System.Text.Json;
using Platform.Modules.Identity.Contracts;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Identity;

/// <summary>
/// Reads Keycloak organization membership. The built-in "organization" scope emits ["acme"], which arrives as one claim
/// per alias; with "add organization id" it emits {"acme":{"id":"..."}}. Tenants match on the alias because Keycloak
/// generates organization ids on import (spec section 7). A value that looks like JSON but does not parse, and an array
/// element that is not a string, grant no membership; they never throw.
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

            AddFromJson(value, aliases);
        }

        aliases.Remove(string.Empty);
        return aliases;
    }

    public static bool BelongsTo(ClaimsPrincipal principal, TenantContext tenant) =>
        Aliases(principal).Contains(tenant.KeycloakOrgAlias);

    private static void AddFromJson(string value, HashSet<string> aliases)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(value);
        }
        catch (JsonException)
        {
            // Malformed claim: deliberately no membership. The caller denies and audits the request.
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in root.EnumerateObject())
                {
                    aliases.Add(property.Name);
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        aliases.Add(item.GetString()!);
                    }
                }
            }
        }
    }
}
