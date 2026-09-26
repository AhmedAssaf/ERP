using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Identity.Keycloak;
using Platform.Modules.Identity.Members;
using Platform.Shared.Results;

namespace Platform.IntegrationTests.Identity;

/// <summary>
/// F-06 as narrowed (docs/05 row 3, spec D-4, D-5 and 4.2) against a real Keycloak 26.3 with the repository's tenant realm
/// and Mailpit on the same Docker network: an invitation creates or finds the Keycloak user, adds them to the tenant's
/// organization, writes an invited member row with the roles, and Keycloak emails the set-password-and-TOTP link; the
/// realm's brute-force detection locks an account after three wrong TOTP codes. Each test uses its own email address.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed partial class StaffInvitationTests(DatabaseFixture db, KeycloakFixture keycloak) : IClassFixture<KeycloakFixture>
{
    private const string Admin = "admin-actor";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Inviting_creates_the_user_adds_them_to_the_organization_and_emails_them()
    {
        var email = Unique("sara");
        await using var host = Host();

        Result<Invitation> result;
        await using (var scope = host.ScopeFor(TestTenants.Acme))
        {
            var staff = scope.ServiceProvider.GetRequiredService<IStaffService>();
            result = await staff.InviteAsync(email, "Sara Ahmed", [TenantRoles.ContractsOfficer, TenantRoles.TechnicalEvaluator], Admin, Ct);
        }

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        result.Value.Email.ShouldBe(InvitationEmail.Sent);
        var userId = result.Value.Member.UserId.ShouldNotBeNull();

        var row = (await MemberRows.FindByEmailAsync(db.AppConnectionString, TestTenants.Acme.TenantId, email, Ct)).ShouldNotBeNull();
        row.Status.ShouldBe("invited");
        row.UserId.ShouldBe(userId);
        row.Roles.ShouldBe([TenantRoles.ContractsOfficer, TenantRoles.TechnicalEvaluator]);

        var users = await keycloak.AdminGetAsync($"users?email={Uri.EscapeDataString(email)}&exact=true", Ct);
        users.GetArrayLength().ShouldBe(1);
        users[0].GetProperty("id").GetString().ShouldBe(userId);
        users[0].GetProperty("firstName").GetString().ShouldBe("Sara");
        users[0].GetProperty("lastName").GetString().ShouldBe("Ahmed");
        (await OrganizationAliasesAsync(userId)).ShouldBe(["acme"]);

        var message = (await MessagesToAsync(email)).ShouldHaveSingleItem();
        var token = ActionToken(message);
        token.GetProperty("typ").GetString().ShouldBe("execute-actions");
        // The link lands the user on the tenant's host (registered exactly on waslabid-web) and lives for 72 hours.
        token.GetProperty("reduri").GetString().ShouldBe("https://acme.localhost:8443/");
        (token.GetProperty("exp").GetInt64() - token.GetProperty("iat").GetInt64()).ShouldBe(72 * 3600);
        (await MemberRows.AuditCountAsync(db.OwnerConnectionString, TestTenants.Acme.TenantId, Admin, "identity.member_invited", Ct)).ShouldBeGreaterThanOrEqualTo(1);
        (await InvitedAuditsForAsync(TestTenants.Acme.TenantId, userId)).ShouldBe(1);
        (await InvitedAuditFieldAsync(TestTenants.Acme.TenantId, userId, "existing_account")).ShouldBe("false");
    }

    [Fact]
    public async Task An_invitee_sets_a_password_and_totp_from_the_email_then_signs_in_and_becomes_active()
    {
        // F-06: given an invitation, when the invitee sets a password and enrols TOTP, then they can log in.
        var email = Unique("layla");
        const string password = "Invitee-Passw0rd-1";
        await using (var host = Host())
        await using (var scope = host.ScopeFor(TestTenants.Acme))
        {
            var invited = await scope.ServiceProvider.GetRequiredService<IStaffService>()
                .InviteAsync(email, "Layla Hassan", [TenantRoles.ContractsOfficer], Admin, Ct);
            invited.IsSuccess.ShouldBeTrue(invited.IsSuccess ? null : invited.Error.Message);
        }

        using var browser = new KeycloakBrowser(keycloak.BaseAddress);
        var link = ActionLink().Match((await MessagesToAsync(email)).ShouldHaveSingleItem());
        link.Success.ShouldBeTrue("the email carries the action-token link");
        var page = (await browser.NavigateAsync(new Uri(link.Value), Ct)).Page.ShouldNotBeNull();
        if (!KeycloakBrowser.HasForm(page, "kc-totp-settings-form"))
        {
            // Keycloak first lists the actions and asks the invitee to proceed.
            var proceed = KeycloakBrowser.LinkContaining(page, "action-token").ShouldNotBeNull(KeycloakBrowser.Feedback(page));
            page = (await browser.NavigateAsync(proceed, Ct)).Page.ShouldNotBeNull();
        }

        KeycloakBrowser.HasForm(page, "kc-totp-settings-form").ShouldBeTrue(KeycloakBrowser.Feedback(page));
        var secret = KeycloakBrowser.InputValue(page, "totpSecret").ShouldNotBeNull();
        var totp = new OtpNet.Totp(Encoding.UTF8.GetBytes(secret));
        page = (await browser.SubmitAsync(page, "kc-totp-settings-form", new Dictionary<string, string>
        {
            ["totp"] = totp.ComputeTotp(),
            ["totpSecret"] = secret,
            ["userLabel"] = "phone",
        }, Ct)).Page.ShouldNotBeNull();
        KeycloakBrowser.HasForm(page, "kc-passwd-update-form").ShouldBeTrue(KeycloakBrowser.Feedback(page));
        page = (await browser.SubmitAsync(page, "kc-passwd-update-form", new Dictionary<string, string>
        {
            ["password-new"] = password,
            ["password-confirm"] = password,
        }, Ct)).Page.ShouldNotBeNull();
        // The last page offers the way back to the tenant's host.
        KeycloakBrowser.LinkContaining(page, "https://acme.localhost:8443/").ShouldNotBeNull(KeycloakBrowser.Feedback(page));

        await using var factory = WebFactory();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://acme.localhost"), AllowAutoRedirect = false });
        using var login = new KeycloakBrowser(keycloak.BaseAddress);
        var otpPage = await PasswordStepAsync(client, login, email, password);
        // The setup already used this period's code, which Keycloak will not take twice; the policy's look-ahead
        // window accepts the next one.
        var done = await login.SubmitAsync(otpPage, "kc-otp-login-form", Otp(totp.ComputeTotp(DateTime.UtcNow.AddSeconds(30))), Ct);
        var callback = done.Callback.ShouldNotBeNull(done.Page is null ? null : KeycloakBrowser.Feedback(done.Page));
        using var callbackRequest = new HttpRequestMessage(callback.Method, callback.Url.PathAndQuery);
        if (callback.Method == HttpMethod.Post)
        {
            callbackRequest.Content = new FormUrlEncodedContent(callback.Form);
        }

        using var signedIn = await client.SendAsync(callbackRequest, Ct);
        signedIn.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        using var home = await client.GetAsync(signedIn.Headers.Location.ShouldNotBeNull(), Ct);

        home.StatusCode.ShouldBe(HttpStatusCode.OK);
        var row = (await MemberRows.FindByEmailAsync(db.AppConnectionString, TestTenants.Acme.TenantId, email, Ct)).ShouldNotBeNull();
        row.Status.ShouldBe("active");
        row.ActivatedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Inviting_an_existing_user_adds_membership_without_a_duplicate_user()
    {
        var email = Unique("omar");
        await using var host = Host();
        string firstUserId;
        await using (var scope = host.ScopeFor(TestTenants.Beta))
        {
            var first = await scope.ServiceProvider.GetRequiredService<IStaffService>()
                .InviteAsync(email, "Omar Khalid", [TenantRoles.FinanceApprover], Admin, Ct);
            first.IsSuccess.ShouldBeTrue(first.IsSuccess ? null : first.Error.Message);
            firstUserId = first.Value.Member.UserId.ShouldNotBeNull();
        }

        Result<Invitation> second;
        await using (var scope = host.ScopeFor(TestTenants.Acme))
        {
            second = await scope.ServiceProvider.GetRequiredService<IStaffService>()
                .InviteAsync(email.ToUpperInvariant(), "Omar Khalid", [TenantRoles.TenantAdmin], Admin, Ct);
        }

        second.IsSuccess.ShouldBeTrue(second.IsSuccess ? null : second.Error.Message);
        (await InvitedAuditFieldAsync(TestTenants.Acme.TenantId, firstUserId, "existing_account")).ShouldBe("true");
        second.Value.Member.UserId.ShouldBe(firstUserId);
        (await keycloak.AdminGetAsync($"users?email={Uri.EscapeDataString(email)}&exact=true", Ct)).GetArrayLength().ShouldBe(1);
        (await OrganizationAliasesAsync(firstUserId)).Order(StringComparer.Ordinal).ShouldBe(["acme", "beta"]);
        var acmeRow = (await MemberRows.FindByEmailAsync(db.AppConnectionString, TestTenants.Acme.TenantId, email, Ct)).ShouldNotBeNull();
        acmeRow.UserId.ShouldBe(firstUserId);
        acmeRow.Roles.ShouldBe([TenantRoles.TenantAdmin]);
        // The account has no password or OTP yet, so the second tenant's invitation sends the setup link again.
        (await MessagesToAsync(email)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Inviting_an_existing_set_up_account_emails_them_one_notice_and_answers_as_for_a_new_account()
    {
        // Tenant isolation: the inviting admin cannot tell whether the address already had a WaslaBid account, and the
        // person is never added to a tenant without being told.
        var email = KeycloakFixture.EmailOf(KeycloakFixture.ReadyUser);
        var inviter = $"inviter-{Guid.NewGuid():N}";
        var inviterEmail = $"hala.{Guid.NewGuid():N}@beta.test";
        await MemberRows.InsertAsync(db.AppConnectionString, TestTenants.Beta.TenantId, inviter, inviterEmail, [TenantRoles.TenantAdmin], "active", Ct);
        await using var host = Host();

        Result<Invitation> fresh;
        Result<Invitation> existing;
        await using (var scope = host.ScopeFor(TestTenants.Beta))
        {
            var staff = scope.ServiceProvider.GetRequiredService<IStaffService>();
            fresh = await staff.InviteAsync(Unique("fresh"), "Fresh Person", [TenantRoles.ContractsOfficer], inviter, Ct);
            existing = await staff.InviteAsync(email, "Ready Person", [TenantRoles.ContractsOfficer, TenantRoles.FinanceApprover], inviter, Ct);
        }

        fresh.IsSuccess.ShouldBeTrue(fresh.IsSuccess ? null : fresh.Error.Message);
        existing.IsSuccess.ShouldBeTrue(existing.IsSuccess ? null : existing.Error.Message);
        existing.Value.Email.ShouldBe(fresh.Value.Email);
        existing.Value.Email.ShouldBe(InvitationEmail.Sent);
        var userId = existing.Value.Member.UserId.ShouldNotBeNull();
        (await InvitedAuditFieldAsync(TestTenants.Beta.TenantId, userId, "existing_account")).ShouldBe("true");
        (await OrganizationAliasesAsync(userId)).ShouldBe(["beta"]);

        var notice = (await MessagesToAsync(email)).ShouldHaveSingleItem();
        ActionLink().IsMatch(notice).ShouldBeFalse("a set-up account gets a notice, not a setup link");
        var inviterName = inviterEmail.Split('@')[0];
        var portal = TestTenants.Beta.Branding.PortalName;
        notice.ShouldContain($"{inviterName} added you to {portal} on WaslaBid as Contracts officer, Finance approver.");
        notice.ShouldContain("Sign in at https://beta.localhost:8443/");
        notice.ShouldContain($"If you do not expect this, ignore this email and tell {portal}.");
        notice.ShouldContain($"أضافك {inviterName} إلى {portal} على WaslaBid");
    }

    [Fact]
    public async Task Inviting_a_disabled_account_is_refused_audited_and_adds_nothing()
    {
        var email = KeycloakFixture.EmailOf(KeycloakFixture.DisabledUser);
        await using var host = Host();

        Result<Invitation> result;
        await using (var scope = host.ScopeFor(TestTenants.Acme))
        {
            result = await scope.ServiceProvider.GetRequiredService<IStaffService>()
                .InviteAsync(email, "Disabled Person", [TenantRoles.ContractsOfficer], Admin, Ct);
        }

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe("identity.account_disabled");
        result.Error.Message.ShouldNotContain("disabled", Case.Insensitive);
        (await MemberRows.FindByEmailAsync(db.AppConnectionString, TestTenants.Acme.TenantId, email, Ct)).ShouldBeNull();
        var userId = (await keycloak.AdminGetAsync($"users?username={KeycloakFixture.DisabledUser}&exact=true", Ct))[0].GetProperty("id").GetString()!;
        (await OrganizationAliasesAsync(userId)).ShouldBeEmpty();
        (await MessagesToAsync(email)).ShouldBeEmpty();
        (await RefusedAuditFieldAsync(TestTenants.Acme.TenantId, email, "reason")).ShouldBe("account_disabled");
    }

    [Fact]
    public async Task A_name_keycloak_refuses_is_reported_as_an_invalid_name()
    {
        await using var host = Host(services => services.AddHttpClient<KeycloakAdminClient>()
            .AddHttpMessageHandler(() => new RefuseUserCreation()));
        var email = Unique("refused");

        Result<Invitation> result;
        await using (var scope = host.ScopeFor(TestTenants.Acme))
        {
            result = await scope.ServiceProvider.GetRequiredService<IStaffService>()
                .InviteAsync(email, "Refused Name", [TenantRoles.ContractsOfficer], Admin, Ct);
        }

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe("identity.invalid_display_name");
        (await MemberRows.FindByEmailAsync(db.AppConnectionString, TestTenants.Acme.TenantId, email, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task A_400_that_is_not_about_the_name_is_reported_as_the_generic_failure()
    {
        // The lookup that finds no existing user still gets a token; the create-user request is then refused with 401
        // as if Keycloak had just revoked it, and renewing the token for the retry fails with 400 (the service
        // account's credentials stopped working, say) before the create-user request, the one that carries the name,
        // is ever retried.
        await using var host = Host(services => services.AddHttpClient<KeycloakAdminClient>()
            .AddHttpMessageHandler(() => new RefuseTokenRenewal()));
        var email = Unique("renewal");

        Result<Invitation> result;
        await using (var scope = host.ScopeFor(TestTenants.Acme))
        {
            result = await scope.ServiceProvider.GetRequiredService<IStaffService>()
                .InviteAsync(email, "Renewal Failure", [TenantRoles.ContractsOfficer], Admin, Ct);
        }

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe("identity.invitation_failed");
        (await MemberRows.FindByEmailAsync(db.AppConnectionString, TestTenants.Acme.TenantId, email, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task When_the_member_row_cannot_be_saved_the_new_organization_membership_is_removed()
    {
        var email = Unique("compensate");
        await using var host = Host(FailMemberSaves);

        Result<Invitation> result;
        await using (var scope = host.ScopeFor(TestTenants.Acme))
        {
            result = await scope.ServiceProvider.GetRequiredService<IStaffService>()
                .InviteAsync(email, "Never Saved", [TenantRoles.ContractsOfficer], Admin, Ct);
        }

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe("identity.invitation_failed");
        (await MemberRows.FindByEmailAsync(db.AppConnectionString, TestTenants.Acme.TenantId, email, Ct)).ShouldBeNull();
        var users = await keycloak.AdminGetAsync($"users?email={Uri.EscapeDataString(email)}&exact=true", Ct);
        users.GetArrayLength().ShouldBe(1);
        var userId = users[0].GetProperty("id").GetString()!;
        (await OrganizationAliasesAsync(userId)).ShouldBeEmpty("the membership added for this invitation is taken back");
        (await MessagesToAsync(email)).ShouldBeEmpty();
    }

    [Fact]
    public async Task When_the_member_row_cannot_be_saved_an_earlier_membership_is_kept()
    {
        var email = Unique("keep");
        string userId;
        await using (var host = Host())
        await using (var scope = host.ScopeFor(TestTenants.Beta))
        {
            var first = await scope.ServiceProvider.GetRequiredService<IStaffService>()
                .InviteAsync(email, "Kept Member", [TenantRoles.ContractsOfficer], Admin, Ct);
            userId = first.Value.Member.UserId.ShouldNotBeNull();
        }

        await using (var failing = Host(FailMemberSaves))
        await using (var scope = failing.ScopeFor(TestTenants.Acme))
        {
            var second = await scope.ServiceProvider.GetRequiredService<IStaffService>()
                .InviteAsync(email, "Kept Member", [TenantRoles.ContractsOfficer], Admin, Ct);
            second.IsSuccess.ShouldBeFalse();
            second.Error.Code.ShouldBe("identity.invitation_failed");
        }

        (await OrganizationAliasesAsync(userId)).ShouldBe(["beta"]);
    }

    [Fact]
    public async Task Inviting_a_member_of_the_tenant_again_is_a_conflict_and_nothing_is_sent()
    {
        var email = Unique("twice");
        await using var host = Host();
        await using var scope = host.ScopeFor(TestTenants.Acme);
        var staff = scope.ServiceProvider.GetRequiredService<IStaffService>();
        (await staff.InviteAsync(email, "Twice Invited", [TenantRoles.ContractsOfficer], Admin, Ct)).IsSuccess.ShouldBeTrue();

        var again = await staff.InviteAsync(email, "Twice Invited", [TenantRoles.ContractsOfficer], Admin, Ct);

        again.IsSuccess.ShouldBeFalse();
        again.Error.Code.ShouldBe("identity.member_exists");
        (await MessagesToAsync(email)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Resending_an_invitation_sends_the_link_again_and_is_audited()
    {
        var email = Unique("resend");
        await using var host = Host();
        await using var scope = host.ScopeFor(TestTenants.Acme);
        var staff = scope.ServiceProvider.GetRequiredService<IStaffService>();
        var invited = await staff.InviteAsync(email, "Resend Me", [TenantRoles.ContractsOfficer], Admin, Ct);
        var userId = invited.Value.Member.UserId.ShouldNotBeNull();

        var resent = await staff.ResendAsync(userId, Admin, Ct);

        resent.IsSuccess.ShouldBeTrue(resent.IsSuccess ? null : resent.Error.Message);
        resent.Value.Email.ShouldBe(InvitationEmail.Sent);
        (await MessagesToAsync(email)).Count.ShouldBe(2);
        (await MemberRows.AuditCountAsync(db.OwnerConnectionString, TestTenants.Acme.TenantId, Admin, "identity.invitation_resent", Ct)).ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task The_member_count_of_an_organization_comes_from_keycloak()
    {
        await using var host = Host();
        await using var scope = host.PlatformScope();
        var members = scope.ServiceProvider.GetRequiredService<IOrganizationMembers>();

        var expected = (await keycloak.AdminGetAsync("organizations?briefRepresentation=true", Ct)).EnumerateArray()
            .Single(o => o.GetProperty("alias").GetString() == "acme").GetProperty("id").GetString();
        var count = await members.CountAsync("acme", Ct);

        count.ShouldBe((await keycloak.AdminGetAsync($"organizations/{expected}/members/count", Ct)).GetInt32());
        count.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(3);
        (await members.CountAsync("no-such-organization", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task A_correct_totp_code_completes_the_tenant_login()
    {
        // The control for the lockout test: the seeded credential and the browser flow work, so a refusal there is the lockout.
        await using var factory = WebFactory();
        using var browser = new KeycloakBrowser(keycloak.BaseAddress);
        var otpPage = await PasswordStepAsync(factory, browser, KeycloakFixture.OtpUser);

        var done = await browser.SubmitAsync(otpPage, "kc-otp-login-form", Otp(KeycloakFixture.CurrentOtp()), Ct);

        done.Callback.ShouldNotBeNull(done.Page is null ? null : KeycloakBrowser.Feedback(done.Page))
            .Url.GetLeftPart(UriPartial.Path).ShouldBe("https://acme.localhost/signin-oidc");
    }

    [Fact]
    public async Task Three_wrong_totp_codes_lock_the_account()
    {
        await using var factory = WebFactory();
        using var browser = new KeycloakBrowser(keycloak.BaseAddress);
        var page = await PasswordStepAsync(factory, browser, KeycloakFixture.LockoutUser);
        var userId = (await keycloak.AdminGetAsync($"users?username={KeycloakFixture.LockoutUser}&exact=true", Ct))[0].GetProperty("id").GetString();
        var right = KeycloakFixture.CurrentOtp();
        var wrong = ((int.Parse(right, CultureInfo.InvariantCulture) + 500_000) % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            // Keycloak also locks on two failures less than a second apart (its quick-login check), which would hide
            // whether the third failure is what locks; the attempts are spaced so only failureFactor 3 can.
            await Task.Delay(TimeSpan.FromMilliseconds(1100), Ct);
            var step = await browser.SubmitAsync(page, "kc-otp-login-form", Otp(wrong), Ct);
            step.Callback.ShouldBeNull();
            page = step.Page.ShouldNotBeNull();
            var status = await keycloak.AdminGetAsync($"attack-detection/brute-force/users/{userId}", Ct);
            status.GetProperty("numFailures").GetInt32().ShouldBe(attempt);
            status.GetProperty("disabled").GetBoolean().ShouldBe(attempt == 3, $"locked after {attempt} wrong codes");
        }

        var afterRight = await browser.SubmitAsync(page, "kc-otp-login-form", Otp(right), Ct);

        afterRight.Callback.ShouldBeNull("the right code must be refused while the account is locked");
        (await keycloak.AdminGetAsync($"attack-detection/brute-force/users/{userId}", Ct)).GetProperty("disabled").GetBoolean().ShouldBeTrue();
    }

    private ModuleHost Host(Action<IServiceCollection>? configure = null) =>
        new(db.AppConnectionString, KeycloakAdminSettings(keycloak), configure: configure);

    internal static IConfiguration KeycloakAdminSettings(KeycloakFixture keycloak) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KeycloakAdmin:BaseUrl"] = keycloak.BaseAddress,
            ["KeycloakAdmin:ClientSecret"] = KeycloakFixture.AdminApiSecret,
            ["KeycloakAdmin:TenantUrl"] = "https://{slug}.localhost:8443/",
            ["Smtp:Host"] = keycloak.MailpitSmtp.Host,
            ["Smtp:Port"] = keycloak.MailpitSmtp.Port.ToString(CultureInfo.InvariantCulture),
            ["Smtp:From"] = "no-reply@waslabid.test",
        })
        .Build();

    private PlatformWebFactory WebFactory() => new(db.AppConnectionString, new OidcSettings(keycloak.Authority, KeycloakFixture.WebClientSecret));

    /// <summary>Starts a tenant login from the app's own challenge and submits the password; returns the OTP page.</summary>
    private static async Task<string> PasswordStepAsync(PlatformWebFactory factory, KeycloakBrowser browser, string username)
    {
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://acme.localhost"), AllowAutoRedirect = false });
        return await PasswordStepAsync(client, browser, username, KeycloakFixture.UserPassword);
    }

    private static async Task<string> PasswordStepAsync(HttpClient client, KeycloakBrowser browser, string username, string password)
    {
        using var challenge = await client.GetAsync(new Uri("/", UriKind.Relative), Ct);
        challenge.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var page = await browser.OpenAsync(challenge.Headers.Location.ShouldNotBeNull(), Ct);
        var fields = new Dictionary<string, string> { ["username"] = username, ["password"] = password };

        // Organizations make the login identity-first: the username may be asked for on its own before the password.
        for (var step = 0; step < 2 && !KeycloakBrowser.HasForm(page, "kc-otp-login-form"); step++)
        {
            var next = await browser.SubmitAsync(page, "kc-form-login", fields, Ct);
            next.Callback.ShouldBeNull("the password alone must not complete a tenant login");
            page = next.Page.ShouldNotBeNull();
        }

        KeycloakBrowser.HasForm(page, "kc-otp-login-form").ShouldBeTrue(KeycloakBrowser.Feedback(page));
        return page;
    }

    private static Dictionary<string, string> Otp(string code) => new() { ["otp"] = code };

    private async Task<IReadOnlyList<string>> OrganizationAliasesAsync(string userId) =>
        [.. (await keycloak.AdminGetAsync($"organizations/members/{userId}/organizations?briefRepresentation=true", Ct))
            .EnumerateArray().Select(o => o.GetProperty("alias").GetString()!)];

    private async Task<IReadOnlyList<string>> MessagesToAsync(string email)
    {
        using var http = new HttpClient { BaseAddress = keycloak.MailpitApi };
        // Keycloak sends in the request; allow Mailpit a moment to store the message.
        for (var attempt = 0; ; attempt++)
        {
            var search = await http.GetFromJsonAsync<JsonElement>(
                new Uri($"api/v1/search?query={Uri.EscapeDataString($"to:\"{email}\"")}", UriKind.Relative), Ct);
            var ids = search.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("ID").GetString()!).ToList();
            if (ids.Count > 0 || attempt >= 10)
            {
                var texts = new List<string>();
                foreach (var id in ids)
                {
                    var message = await http.GetFromJsonAsync<JsonElement>(new Uri($"api/v1/message/{id}", UriKind.Relative), Ct);
                    texts.Add(message.GetProperty("Text").GetString()!);
                }

                return texts;
            }

            await Task.Delay(300, Ct);
        }
    }

    private static JsonElement ActionToken(string messageText)
    {
        var link = ActionLink().Match(messageText);
        link.Success.ShouldBeTrue("the email carries the action-token link");
        var payload = link.Groups[1].Value.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
        using var json = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
        return json.RootElement.Clone();
    }

    private async Task<int> InvitedAuditsForAsync(Guid tenantId, string userId)
    {
        await using var connection = new Npgsql.NpgsqlConnection(db.OwnerConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand(
            "select count(*) from audit.events where tenant_id = @tenant and action = 'identity.member_invited' and subject_id = @user", connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("user", userId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture);
    }

    private async Task<string?> InvitedAuditFieldAsync(Guid tenantId, string userId, string field)
    {
        await using var connection = new Npgsql.NpgsqlConnection(db.OwnerConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand(
            "select data ->> @field from audit.events where tenant_id = @tenant and action = 'identity.member_invited' and subject_id = @user order by occurred_at desc limit 1",
            connection);
        command.Parameters.AddWithValue("field", field);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("user", userId);
        return await command.ExecuteScalarAsync(Ct) as string;
    }

    private async Task<string?> RefusedAuditFieldAsync(Guid tenantId, string email, string field)
    {
        await using var connection = new Npgsql.NpgsqlConnection(db.OwnerConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand(
            "select data ->> @field from audit.events where tenant_id = @tenant and action = 'identity.invitation_refused' and data ->> 'email' = @email order by occurred_at desc limit 1",
            connection);
        command.Parameters.AddWithValue("field", field);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("email", email);
        return await command.ExecuteScalarAsync(Ct) as string;
    }

    private static void FailMemberSaves(IServiceCollection services) =>
        services.ConfigureDbContext<MembersDbContext>(o => o.AddInterceptors(new FailingSave()));

    /// <summary>The member store failing on save: every SaveChanges of the members context throws.</summary>
    private sealed class FailingSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            throw new DbUpdateException("Simulated failure of the member store.");
    }

    /// <summary>Keycloak answering 400 to user creation, as its user profile does for a name it will not take.</summary>
    private sealed class RefuseUserCreation : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/users", StringComparison.Ordinal)
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest))
                : base.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// The first token request succeeds and the lookup finds no user; the create-user request is then answered 401, and
    /// the token request the client sends to renew it for a retry is answered 400. The create-user request itself is
    /// never sent a second time, so its body (with the name) is never the thing Keycloak refused.
    /// </summary>
    private sealed class RefuseTokenRenewal : DelegatingHandler
    {
        private int _tokenCalls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.Contains("openid-connect/token", StringComparison.Ordinal))
            {
                return Task.FromResult(Interlocked.Increment(ref _tokenCalls) == 1
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { access_token = "token", expires_in = 300 }) }
                    : new HttpResponseMessage(HttpStatusCode.BadRequest));
            }

            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/users", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<object>()) });
            }

            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/users", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }

            return base.SendAsync(request, cancellationToken);
        }
    }

    private static string Unique(string name) => $"{name}.{Guid.NewGuid():N}@invited.waslabid.test";

    [GeneratedRegex(@"https?://\S+action-token\?key=([A-Za-z0-9_\-\.]+)")]
    private static partial Regex ActionLink();
}
