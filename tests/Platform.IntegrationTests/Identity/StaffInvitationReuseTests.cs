using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;

namespace Platform.IntegrationTests.Identity;

/// <summary>
/// QA pass, F-06: the set-password-and-TOTP link in the invitation email works once. After the invitee has used it, the
/// same link, opened again in a fresh browser (someone who later finds the email), must not offer to set a new password
/// or enrol a new authenticator. Expiry itself (72 hours) is asserted from the token by
/// <c>Inviting_creates_the_user_adds_them_to_the_organization_and_emails_them</c>; waiting it out is not automated.
/// </summary>
public sealed partial class StaffInvitationTests
{
    [Fact]
    public async Task An_invitation_link_already_used_cannot_set_a_password_or_totp_again()
    {
        var email = Unique("reuse");
        await using (var host = Host())
        await using (var scope = host.ScopeFor(TestTenants.Acme))
        {
            var invited = await scope.ServiceProvider.GetRequiredService<IStaffService>()
                .InviteAsync(email, "Reem Salem", [TenantRoles.TechnicalEvaluator], Admin, Ct);
            invited.IsSuccess.ShouldBeTrue(invited.IsSuccess ? null : invited.Error.Message);
        }

        var link = new Uri(ActionLink().Match((await MessagesToAsync(email)).ShouldHaveSingleItem()).Value);
        using (var first = new KeycloakBrowser(keycloak.BaseAddress))
        {
            var page = await OpenActionsAsync(first, link);
            KeycloakBrowser.HasForm(page, "kc-totp-settings-form").ShouldBeTrue(KeycloakBrowser.Feedback(page));
            var secret = KeycloakBrowser.InputValue(page, "totpSecret").ShouldNotBeNull();
            page = (await first.SubmitAsync(page, "kc-totp-settings-form", new Dictionary<string, string>
            {
                ["totp"] = new OtpNet.Totp(Encoding.UTF8.GetBytes(secret)).ComputeTotp(),
                ["totpSecret"] = secret,
                ["userLabel"] = "phone",
            }, Ct)).Page.ShouldNotBeNull();
            page = (await first.SubmitAsync(page, "kc-passwd-update-form", new Dictionary<string, string>
            {
                ["password-new"] = "Reused-Link-Passw0rd-1",
                ["password-confirm"] = "Reused-Link-Passw0rd-1",
            }, Ct)).Page.ShouldNotBeNull();
            KeycloakBrowser.LinkContaining(page, "https://acme.localhost:8443/").ShouldNotBeNull(KeycloakBrowser.Feedback(page));
        }

        // A fresh browser: its own cookies, following Keycloak's redirects, keeping whatever page Keycloak ends on.
        using var second = new HttpClient(new HttpClientHandler { UseCookies = true, AllowAutoRedirect = true });
        using var response = await second.GetAsync(link, Ct);
        var again = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest, KeycloakBrowser.Feedback(again));
        KeycloakBrowser.HasForm(again, "kc-totp-settings-form").ShouldBeFalse(KeycloakBrowser.Feedback(again));
        KeycloakBrowser.HasForm(again, "kc-passwd-update-form").ShouldBeFalse(KeycloakBrowser.Feedback(again));
        KeycloakBrowser.LinkContaining(again, "action-token").ShouldBeNull("the used link must not offer to proceed again");
    }

    /// <summary>Opens the email's link and, when Keycloak first lists the actions, follows its proceed link.</summary>
    private static async Task<string> OpenActionsAsync(KeycloakBrowser browser, Uri link)
    {
        var page = (await browser.NavigateAsync(link, Ct)).Page.ShouldNotBeNull();
        if (!KeycloakBrowser.HasForm(page, "kc-totp-settings-form") && KeycloakBrowser.LinkContaining(page, "action-token") is { } proceed)
        {
            page = (await browser.NavigateAsync(proceed, Ct)).Page.ShouldNotBeNull();
        }

        return page;
    }
}
