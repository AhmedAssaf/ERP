using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Vendors.Contracts;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// QA pass on PR #5 (W-33 with W-21, docs/09 W-33 acceptance): what a vendor reads on <c>/vendor/join</c> when the join is
/// refused, through the web host in Arabic (right to left) and English. A claimant whose uphold's add to this tenant's
/// organization failed and waits for the platform admin's retry reads that WaslaBid is still giving them access
/// (<c>vendor.membership_pending_retry</c>), never that their access was removed; a user the tenant removed reads that only
/// the tenant restores it (<c>vendor.membership_removed</c>). The service-level answers are in <c>CrDisputeTriageTests</c>
/// and <c>CrDisputeRetryTests</c>; these check the page's wording.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed partial class VendorJoinRefusalPageTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("en", "<html lang=\"en\" dir=\"ltr\">",
        "WaslaBid moved your company to your account and is still giving you access to Beta Industries. Try again later; there is nothing for you to do.",
        "was removed")]
    [InlineData("ar", "<html lang=\"ar\" dir=\"rtl\">",
        "نقلت وصلة بد منشأتك إلى حسابك وما زالت تمنحك الوصول إلى Beta Industries. حاول لاحقًا، ولا يلزمك أي إجراء.",
        "أُلغي وصولك")]
    public async Task A_claimant_whose_add_waits_for_the_retry_reads_that_access_is_still_being_given_in_their_language(
        string locale, string htmlTag, string expected, string notExpected)
    {
        var crNumber = VendorRows.NewCrNumber();
        var companyId = await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Acme, Guid.NewGuid().ToString(), crNumber, "Tihama Food Industries", Ct);
        await VendorRows.RelateAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, companyId, Ct);
        var claimant = Guid.NewGuid().ToString();
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        var upholdAccounts = new FakeVendorAccounts();
        upholdAccounts.FailingOrganizations[TestTenants.Beta.KeycloakOrgAlias] = true;
        await using (var host = new ModuleHost(db.AppConnectionString, configure: s => s.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => upholdAccounts))))
        {
            Guid disputeId;
            await using (var claim = host.ScopeFor(TestTenants.Acme, actingUserId: claimant))
            {
                disputeId = (await claim.ServiceProvider.GetRequiredService<ICrDisputes>().RaiseAsync(
                    new CrDisputeRequest(crNumber, "Our CR certificate names our general manager.", VendorPrivacyNotice.CurrentVersion, VendorPrivacyNotice.English),
                    $"{claimant}@claimant.test", "Abdullah Al-Ghamdi", Ct)).Value;
            }

            await using var console = host.PlatformScope(admin);
            (await console.ServiceProvider.GetRequiredService<ICrOwnershipAdministration>().UpholdAsync(disputeId, "Checked the certificate.", admin, Ct))
                .Value.IdentityProviderUpdated.ShouldBeFalse("beta's add waits for the retry");
        }

        // What Keycloak holds for the claimant after the uphold: the role and acme's organization, not beta's.
        var accounts = new FakeVendorAccounts { State = new(HoldsVendorRole: true, OrganizationAliases: [TestTenants.Acme.KeycloakOrgAlias]) };
        var user = new TestUser(claimant, [TestTenants.Acme.KeycloakOrgAlias], locale, RealmRoles: [IdentityClaims.VendorRealmRole],
            Email: $"{claimant}@claimant.test", EmailVerified: true);

        var html = await JoinAsync(accounts, "http://beta.localhost", user);

        html.ShouldContain(htmlTag);
        html.ShouldContain(expected);
        html.ShouldNotContain(notExpected);
        RawKey().IsMatch(html).ShouldBeFalse("no raw resource key");
        accounts.Steps.ShouldNotContain("add-organization");
    }

    [Theory]
    [InlineData("en", "<html lang=\"en\" dir=\"ltr\">", "Your access to Acme Contracting was removed. Only Acme Contracting can restore it; contact them.", "still giving you access")]
    [InlineData("ar", "<html lang=\"ar\" dir=\"rtl\">", "أُلغي وصولك إلى Acme Contracting، ولا يعيده إلا Acme Contracting. تواصل معها.", "وما زالت تمنحك")]
    public async Task A_member_the_tenant_removed_reads_that_only_the_tenant_restores_access_in_their_language(
        string locale, string htmlTag, string expected, string notExpected)
    {
        var userId = Guid.NewGuid().ToString();
        await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Acme, userId, VendorRows.NewCrNumber(), "Qassim Dates Company", Ct);
        // Related to acme, but the token and Keycloak no longer carry acme's organization: acme removed the user.
        var accounts = new FakeVendorAccounts { State = new(HoldsVendorRole: true, OrganizationAliases: []) };
        var user = new TestUser(userId, [], locale, RealmRoles: [IdentityClaims.VendorRealmRole], Email: $"{userId}@vendor.test", EmailVerified: true);

        var html = await JoinAsync(accounts, "http://acme.localhost", user);

        html.ShouldContain(htmlTag);
        html.ShouldContain(expected);
        html.ShouldNotContain(notExpected);
        RawKey().IsMatch(html).ShouldBeFalse("no raw resource key");
    }

    /// <summary>Opens <c>/vendor/join</c> on <paramref name="host"/>, posts its form, and returns the decoded answer page.</summary>
    private async Task<string> JoinAsync(FakeVendorAccounts accounts, string host, TestUser user)
    {
        await using var factory = new PlatformWebFactory(db.AppConnectionString).WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => accounts))));
        using var client = factory.CreateClient(new() { BaseAddress = new Uri(host), AllowAutoRedirect = false });
        using var page = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/vendor/join").As(user), Ct);
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        var form = JoinForm().Match(await page.Content.ReadAsStringAsync(Ct));
        form.Success.ShouldBeTrue("the page renders the join form");
        var fields = HiddenInputs().Matches(form.Value)
            .ToDictionary(m => WebUtility.HtmlDecode(m.Groups["name"].Value), m => WebUtility.HtmlDecode(m.Groups["value"].Value), StringComparer.Ordinal);
        using var post = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/vendor/join") { Content = new FormUrlEncodedContent(fields) }.As(user), Ct);
        post.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = WebUtility.HtmlDecode(await post.Content.ReadAsStringAsync(Ct));
        html.ShouldContain("data-form-error");
        return html;
    }

    [GeneratedRegex("<form\\b[^>]*data-vendor-join-form[^>]*>.*?</form>", RegexOptions.Singleline)]
    private static partial Regex JoinForm();

    [GeneratedRegex("<input\\b(?=[^>]*type=\"hidden\")(?=[^>]*name=\"(?<name>[^\"]*)\")(?=[^>]*value=\"(?<value>[^\"]*)\")[^>]*>")]
    private static partial Regex HiddenInputs();

    // A shared resource key rendered as itself, as a missing key would render.
    [GeneratedRegex(@"\b(Vendor|Admin)\.[A-Z][A-Za-z]+\.[A-Za-z.]+")]
    private static partial Regex RawKey();
}
