using System.Globalization;
using Microsoft.Extensions.Localization;
using Platform.Shared.Email;

namespace Platform.Modules.Identity.Members;

/// <summary>
/// The email to a person whose account needs no setup when a tenant adds them (F-06, tenant isolation): nobody is added
/// to a tenant without being told. Both languages in one email, the tenant's default culture first, since the person's
/// own choice is not known to the inviting tenant. Strings in Resources/Members/InvitationNotice.{culture}.resx.
/// </summary>
internal sealed class InvitationNotice(IStringLocalizer<InvitationNotice> text)
{
    private static readonly string[] ArabicFirst = ["ar-SA", "en-US"];
    private static readonly string[] EnglishFirst = ["en-US", "ar-SA"];

    /// <param name="to">The person's address.</param>
    /// <param name="inviter">The inviting admin's display name, or null when the tenant has no member row for them.</param>
    /// <param name="portalName">The tenant's portal name (F-02).</param>
    /// <param name="roles">The roles given, as <see cref="Contracts.TenantRoles"/> names.</param>
    /// <param name="home">The tenant's home URL, where the person signs in.</param>
    /// <param name="tenantCulture">The tenant's default culture, whose text comes first.</param>
    public EmailMessage Write(string to, string? inviter, string portalName, IReadOnlyList<string> roles, Uri home, string tenantCulture)
    {
        var order = tenantCulture.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? EnglishFirst : ArabicFirst;
        var subjects = new List<string>(2);
        var bodies = new List<string>(2);
        foreach (var culture in order)
        {
            var (subject, body) = InCulture(culture, () =>
            {
                var roleNames = string.Join(text["RoleSeparator"].Value, roles.Select(r => text[$"Role.{r}"].Value));
                return (
                    text["Subject", portalName].Value,
                    text["Body", inviter ?? text["SomeAdministrator"].Value, portalName, roleNames, home.AbsoluteUri].Value);
            });
            subjects.Add(subject);
            bodies.Add(body);
        }

        return new EmailMessage([to], string.Join(" / ", subjects), string.Join("\n\n", bodies) + "\n");
    }

    private static T InCulture<T>(string culture, Func<T> read)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            return read();
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
}
