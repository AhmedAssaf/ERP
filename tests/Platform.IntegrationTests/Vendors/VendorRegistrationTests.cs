using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.IntegrationTests.Identity;
using Platform.IntegrationTests.Infrastructure;
using Platform.IntegrationTests.Web;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Identity.Keycloak;
using Platform.Modules.Vendors.Contracts;
using Platform.Shared.Results;

namespace Platform.IntegrationTests.Vendors;

/// <summary>
/// Vendor registration against a real Keycloak 26.3 with the repository's tenant realm and Mailpit (vendor plan task 2,
/// F-11, V-3 to V-7, V-14): self-registration from the tenant host with email verification, the company registration
/// that grants the realm role <c>vendor</c> and the host tenant's organization, and the vendor's next sign-in, which needs
/// no second factor (V-4) while staff keep theirs.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed partial class VendorRegistrationTests(DatabaseFixture db, KeycloakFixture keycloak) : IClassFixture<KeycloakFixture>
{
    private const string Password = "Vendor-Passw0rd-1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_realm_allows_self_registration_with_email_verification_and_never_grants_vendor_by_default()
    {
        var realm = await keycloak.AdminGetAsync(string.Empty, Ct);
        realm.GetProperty("registrationAllowed").GetBoolean().ShouldBeTrue();
        realm.GetProperty("verifyEmail").GetBoolean().ShouldBeTrue();
        realm.GetProperty("registrationEmailAsUsername").GetBoolean().ShouldBeTrue();

        (await keycloak.AdminGetAsync($"roles/{IdentityClaims.VendorRealmRole}", Ct)).GetProperty("name").GetString().ShouldBe("vendor");
        var defaults = await keycloak.AdminGetAsync("roles/default-roles-waslabid/composites", Ct);
        defaults.EnumerateArray().Select(r => r.GetProperty("name").GetString()).ShouldNotContain(IdentityClaims.VendorRealmRole);

        var userId = await keycloak.CreateUserAsync(Unique("plain"), Password, emailVerified: true, Ct);
        (await RealmRolesAsync(userId)).ShouldNotContain(IdentityClaims.VendorRealmRole);
    }

    [Fact]
    public async Task An_unverified_vendor_cannot_sign_in_until_the_link_is_clicked()
    {
        var email = Unique("unverified");
        await using var factory = WebFactory();
        using var client = AppClient(factory);

        using (var registration = new KeycloakBrowser(keycloak.BaseAddress))
        {
            var afterForm = await SubmitRegistrationAsync(client, registration, email);
            afterForm.Callback.ShouldBeNull("registration alone must not sign the user in");
            IsVerifyEmailPage(afterForm.Page.ShouldNotBeNull()).ShouldBeTrue(KeycloakBrowser.Feedback(afterForm.Page!));
        }

        // A later sign-in from another browser stops at email verification too (after the second factor the realm asks
        // of an account that holds no vendor role yet).
        using var login = new KeycloakBrowser(keycloak.BaseAddress);
        var page = await SignInUntilStoppedAsync(client, login, email);
        IsVerifyEmailPage(page).ShouldBeTrue(KeycloakBrowser.Feedback(page));

        var link = VerificationLink((await MessagesToAsync(email, atLeast: 2))[0]);
        var verified = await login.NavigateAsync(link, Ct);

        var callback = verified.Callback.ShouldNotBeNull(verified.Page is null ? null : KeycloakBrowser.Feedback(verified.Page));
        using var signedIn = await SendCallbackAsync(client, callback);
        signedIn.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (await keycloak.AdminGetAsync($"users?email={Uri.EscapeDataString(email)}&exact=true", Ct))[0]
            .GetProperty("emailVerified").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Registering_creates_a_pending_relationship_and_organization_membership_and_audits_it()
    {
        var email = Unique("registers");
        var userId = await keycloak.CreateUserAsync(email, Password, emailVerified: true, Ct);
        var input = VendorRegistrationInputTests.Valid();

        var result = await RegisterAsync(input, userId, email);

        var companyId = result.IsSuccess ? result.Value : throw new ShouldAssertException(result.Error.Message);
        var user = (await VendorRows.FindUserAsync(db.OwnerConnectionString, userId, Ct)).ShouldNotBeNull();
        user.CompanyId.ShouldBe(companyId);
        user.Role.ShouldBe("vendor-admin");
        (await VendorRows.RelationshipsAsync(db.OwnerConnectionString, companyId, Ct))
            .ShouldBe(new Dictionary<Guid, string> { [TestTenants.Acme.TenantId] = "pending" });
        (await RealmRolesAsync(userId)).ShouldContain(IdentityClaims.VendorRealmRole);
        (await OrganizationAliasesAsync(userId)).ShouldBe(["acme"]);
        var audit = (await VendorRows.AuditsAsync(db.OwnerConnectionString, TestTenants.Acme.TenantId, userId, "vendor.registered", Ct))
            .ShouldHaveSingleItem();
        audit.SubjectId.ShouldBe(companyId.ToString());
        using var data = JsonDocument.Parse(audit.Data);
        data.RootElement.GetProperty("cr_number").GetString().ShouldBe(input.CrNumber);
        data.RootElement.GetProperty("email").GetString().ShouldBe(email);
        (await VendorRows.AuditsAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, userId, "vendor.registered", Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Registration_requires_the_privacy_notice_and_stores_its_version()
    {
        var email = Unique("privacy");
        var userId = await keycloak.CreateUserAsync(email, Password, emailVerified: true, Ct);

        var refused = await RegisterAsync(VendorRegistrationInputTests.Valid() with { AcceptedPrivacyNotice = null }, userId, email);

        refused.Error.ShouldNotBeNull().Code.ShouldBe(VendorErrors.PrivacyNoticeRequired);
        (await RealmRolesAsync(userId)).ShouldNotContain(IdentityClaims.VendorRealmRole);
        (await OrganizationAliasesAsync(userId)).ShouldBeEmpty();

        var before = DateTimeOffset.UtcNow.AddMinutes(-1);
        var accepted = await RegisterAsync(VendorRegistrationInputTests.Valid(), userId, email);

        accepted.IsSuccess.ShouldBeTrue(accepted.IsSuccess ? null : accepted.Error.Message);
        var user = (await VendorRows.FindUserAsync(db.OwnerConnectionString, userId, Ct)).ShouldNotBeNull();
        user.PrivacyNoticeVersion.ShouldBe(VendorPrivacyNotice.CurrentVersion);
        user.PrivacyAcceptedAt.ShouldBeGreaterThan(before);
    }

    [Fact]
    public async Task A_member_of_another_tenants_organization_cannot_register_a_company()
    {
        var email = Unique("beta.staff");
        var userId = await keycloak.CreateUserAsync(email, Password, emailVerified: true, Ct);
        await using (var host = Host())
        {
            (await host.Services.GetRequiredService<KeycloakAdminClient>().AddToOrganizationAsync("beta", userId, Ct)).ShouldBeTrue();
        }

        var result = await RegisterAsync(VendorRegistrationInputTests.Valid(), userId, email);

        result.Error.ShouldNotBeNull().Code.ShouldBe(VendorErrors.StaffAccount);
        (await VendorRows.FindUserAsync(db.OwnerConnectionString, userId, Ct)).ShouldBeNull();
        (await RealmRolesAsync(userId)).ShouldNotContain(IdentityClaims.VendorRealmRole);
        (await OrganizationAliasesAsync(userId)).ShouldBe(["beta"]);
    }

    [Fact]
    public async Task A_vendor_account_cannot_be_invited_as_staff()
    {
        // V-3: a vendor never gets an identity.members row, so staff policies stay closed to it.
        var email = Unique("vendor.invitee");
        var userId = await keycloak.CreateUserAsync(email, Password, emailVerified: true, Ct);
        (await RegisterAsync(VendorRegistrationInputTests.Valid(), userId, email)).IsSuccess.ShouldBeTrue();

        Result<Invitation> invitation;
        await using (var host = Host())
        await using (var scope = host.ScopeFor(TestTenants.Beta))
        {
            invitation = await scope.ServiceProvider.GetRequiredService<IStaffService>()
                .InviteAsync(email, "Vendor Person", [TenantRoles.ContractsOfficer], "beta-admin-actor", Ct);
        }

        invitation.Error.ShouldNotBeNull().Code.ShouldBe("identity.account_disabled");
        (await MemberRows.FindByEmailAsync(db.AppConnectionString, TestTenants.Beta.TenantId, email, Ct)).ShouldBeNull();
        (await OrganizationAliasesAsync(userId)).ShouldBe(["acme"]);
        var refused = (await VendorRows.AuditsAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, "beta-admin-actor", "identity.invitation_refused", Ct))
            .First(a => a.SubjectId == userId);
        refused.Data.ShouldContain("vendor_account");
    }

    [Fact]
    public async Task A_registered_vendor_signs_in_again_without_a_second_factor_and_reaches_the_vendor_home()
    {
        var email = Unique("journey");
        await using var factory = WebFactory();
        using var client = AppClient(factory);
        using var browser = new KeycloakBrowser(keycloak.BaseAddress);

        // Sign up from the tenant host, verify, and land back on the tenant host's company form.
        var afterForm = await SubmitRegistrationAsync(client, browser, email);
        IsVerifyEmailPage(afterForm.Page.ShouldNotBeNull()).ShouldBeTrue(KeycloakBrowser.Feedback(afterForm.Page!));
        var verified = await browser.NavigateAsync(VerificationLink((await MessagesToAsync(email, atLeast: 1))[0]), Ct);
        using var signedIn = await SendCallbackAsync(client, verified.Callback.ShouldNotBeNull());
        signedIn.Headers.Location.ShouldNotBeNull().OriginalString.ShouldBe("/vendor/register/company");

        using var formPage = await client.GetAsync(new Uri("/vendor/register/company", UriKind.Relative), Ct);
        formPage.StatusCode.ShouldBe(HttpStatusCode.OK);
        var input = VendorRegistrationInputTests.Valid();
        using var registered = await VendorAccessTests.PostRegistrationAsync(client, null, await formPage.Content.ReadAsStringAsync(Ct), input);
        registered.StatusCode.ShouldBe(HttpStatusCode.OK);
        var done = await registered.Content.ReadAsStringAsync(Ct);
        done.ShouldContain("data-vendor-registered");

        // The page signs the user out so the next token carries the vendor role and the organization.
        using var signOut = await PostSignOutAsync(client, done);
        signOut.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var confirm = await browser.NavigateAsync(signOut.Headers.Location.ShouldNotBeNull(), Ct);
        var loggedOut = confirm.Callback ?? (await browser.SubmitFirstFormAsync(confirm.Page.ShouldNotBeNull(), Ct)).Callback.ShouldNotBeNull();
        using var afterSignOut = await SendCallbackAsync(client, loggedOut);
        afterSignOut.Headers.Location.ShouldNotBeNull().OriginalString.ShouldBe("/vendor");

        using var challenge = await client.GetAsync(new Uri("/vendor", UriKind.Relative), Ct);
        challenge.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var page = await browser.OpenAsync(challenge.Headers.Location.ShouldNotBeNull(), Ct);
        KeycloakStep step = new(page, null);
        for (var attempt = 0; attempt < 2 && step.Callback is null; attempt++)
        {
            step = await browser.SubmitAsync(step.Page!, "kc-form-login", new Dictionary<string, string> { ["username"] = email, ["password"] = Password }, Ct);
        }

        var callback = step.Callback.ShouldNotBeNull(step.Page is null ? null : KeycloakBrowser.Feedback(step.Page));
        using var again = await SendCallbackAsync(client, callback);
        using var home = await client.GetAsync(again.Headers.Location.ShouldNotBeNull(), Ct);
        home.StatusCode.ShouldBe(HttpStatusCode.OK);
        // The page shows the company's name in the user's language (the tenant's default when the token names none).
        var html = WebUtility.HtmlDecode(await home.Content.ReadAsStringAsync(Ct));
        (html.Contains(input.NameAr!, StringComparison.Ordinal) || html.Contains(input.NameEn!, StringComparison.Ordinal))
            .ShouldBeTrue("the vendor home shows the registered company");
    }

    private static async Task<KeycloakStep> SubmitRegistrationAsync(HttpClient client, KeycloakBrowser browser, string email)
    {
        using var start = await client.GetAsync(new Uri("/vendor/register", UriKind.Relative), Ct);
        start.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var page = await browser.OpenAsync(start.Headers.Location.ShouldNotBeNull(), Ct);
        KeycloakBrowser.HasForm(page, "kc-register-form").ShouldBeTrue(KeycloakBrowser.Feedback(page));
        return await browser.SubmitAsync(page, "kc-register-form", new Dictionary<string, string>
        {
            ["email"] = email,
            ["firstName"] = "Vendor",
            ["lastName"] = "Applicant",
            ["password"] = Password,
            ["password-confirm"] = Password,
        }, Ct);
    }

    /// <summary>Starts a sign-in from the app's challenge and answers Keycloak until it stops without handing back.</summary>
    private static async Task<string> SignInUntilStoppedAsync(HttpClient client, KeycloakBrowser browser, string email)
    {
        using var challenge = await client.GetAsync(new Uri("/vendor/register/company", UriKind.Relative), Ct);
        challenge.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var page = await browser.OpenAsync(challenge.Headers.Location.ShouldNotBeNull(), Ct);
        for (var step = 0; step < 4; step++)
        {
            KeycloakStep next;
            if (KeycloakBrowser.HasForm(page, "kc-form-login"))
            {
                next = await browser.SubmitAsync(page, "kc-form-login", new Dictionary<string, string> { ["username"] = email, ["password"] = Password }, Ct);
            }
            else if (KeycloakBrowser.HasForm(page, "kc-totp-settings-form"))
            {
                var secret = KeycloakBrowser.InputValue(page, "totpSecret").ShouldNotBeNull();
                next = await browser.SubmitAsync(page, "kc-totp-settings-form", new Dictionary<string, string>
                {
                    ["totp"] = new OtpNet.Totp(Encoding.UTF8.GetBytes(secret)).ComputeTotp(),
                    ["totpSecret"] = secret,
                    ["userLabel"] = "phone",
                }, Ct);
            }
            else
            {
                return page;
            }

            next.Callback.ShouldBeNull("an unverified account must not be signed in");
            page = next.Page.ShouldNotBeNull();
        }

        return page;
    }

    private static async Task<HttpResponseMessage> SendCallbackAsync(HttpClient client, AppCallback callback)
    {
        using var request = new HttpRequestMessage(callback.Method, callback.Url.PathAndQuery);
        if (callback.Method == HttpMethod.Post)
        {
            request.Content = new FormUrlEncodedContent(callback.Form);
        }

        return await client.SendAsync(request, Ct);
    }

    private static Task<HttpResponseMessage> PostSignOutAsync(HttpClient client, string page)
    {
        var form = SignOutForm().Match(page);
        form.Success.ShouldBeTrue("the registered page offers the sign-out");
        var fields = HiddenInputs().Matches(form.Value)
            .ToDictionary(m => WebUtility.HtmlDecode(m.Groups["name"].Value), m => WebUtility.HtmlDecode(m.Groups["value"].Value), StringComparer.Ordinal);
        fields["returnUrl"].ShouldBe("/vendor");
        return client.PostAsync(new Uri("/account/sign-out", UriKind.Relative), new FormUrlEncodedContent(fields), Ct);
    }

    private async Task<Result<Guid>> RegisterAsync(VendorRegistration input, string userId, string email)
    {
        await using var host = Host();
        await using var scope = host.ScopeFor(TestTenants.Acme);
        return await scope.ServiceProvider.GetRequiredService<IVendorRegistration>().RegisterCompanyAsync(input, userId, email, Ct);
    }

    private ModuleHost Host() => new(db.AppConnectionString, StaffInvitationTests.KeycloakAdminSettings(keycloak));

    private WebApplicationFactory<Program> WebFactory() =>
        new PlatformWebFactory(db.AppConnectionString, new OidcSettings(keycloak.Authority, KeycloakFixture.WebClientSecret))
            .WithWebHostBuilder(builder =>
            {
                foreach (var (key, value) in StaffInvitationTests.KeycloakAdminSettings(keycloak).AsEnumerable().ToList())
                {
                    if (value is not null)
                    {
                        builder.UseSetting(key, value);
                    }
                }
            });

    private static HttpClient AppClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new() { BaseAddress = new Uri("https://acme.localhost"), AllowAutoRedirect = false });

    private async Task<IReadOnlyList<string>> RealmRolesAsync(string userId) =>
        [.. (await keycloak.AdminGetAsync($"users/{userId}/role-mappings/realm", Ct)).EnumerateArray().Select(r => r.GetProperty("name").GetString()!)];

    private async Task<IReadOnlyList<string>> OrganizationAliasesAsync(string userId) =>
        [.. (await keycloak.AdminGetAsync($"organizations/members/{userId}/organizations?briefRepresentation=true", Ct))
            .EnumerateArray().Select(o => o.GetProperty("alias").GetString()!)];

    /// <summary>The messages to <paramref name="email"/>, newest first, once at least <paramref name="atLeast"/> arrived.</summary>
    private async Task<IReadOnlyList<string>> MessagesToAsync(string email, int atLeast)
    {
        using var http = new HttpClient { BaseAddress = keycloak.MailpitApi };
        for (var attempt = 0; ; attempt++)
        {
            var search = await http.GetFromJsonAsync<JsonElement>(
                new Uri($"api/v1/search?query={Uri.EscapeDataString($"to:\"{email}\"")}", UriKind.Relative), Ct);
            var messages = search.GetProperty("messages").EnumerateArray()
                .OrderByDescending(m => m.GetProperty("Created").GetDateTimeOffset())
                .Select(m => m.GetProperty("ID").GetString()!)
                .ToList();
            if (messages.Count >= atLeast || attempt >= 20)
            {
                messages.Count.ShouldBeGreaterThanOrEqualTo(atLeast);
                var texts = new List<string>();
                foreach (var id in messages)
                {
                    var message = await http.GetFromJsonAsync<JsonElement>(new Uri($"api/v1/message/{id}", UriKind.Relative), Ct);
                    texts.Add(message.GetProperty("Text").GetString()!);
                }

                return texts;
            }

            await Task.Delay(300, Ct);
        }
    }

    private static Uri VerificationLink(string messageText)
    {
        var link = ActionLink().Match(messageText);
        link.Success.ShouldBeTrue("the email carries the verification link");
        return new Uri(link.Value);
    }

    // Keycloak 26.3's verify-email page carries no template comment; its resend link names the required action.
    private static bool IsVerifyEmailPage(string page) => page.Contains("execution=VERIFY_EMAIL", StringComparison.Ordinal);

    private static string Unique(string name) => $"{name}.{Guid.NewGuid():N}@vendor.waslabid.test";

    [GeneratedRegex(@"https?://\S+action-token\?key=[A-Za-z0-9_\-\.]+\S*")]
    private static partial Regex ActionLink();

    [GeneratedRegex("<form\\b[^>]*data-vendor-sign-out[^>]*>.*?</form>", RegexOptions.Singleline)]
    private static partial Regex SignOutForm();

    [GeneratedRegex("<input\\b(?=[^>]*type=\"hidden\")(?=[^>]*name=\"(?<name>[^\"]*)\")(?=[^>]*value=\"(?<value>[^\"]*)\")[^>]*>")]
    private static partial Regex HiddenInputs();
}
