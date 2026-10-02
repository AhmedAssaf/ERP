using System.Globalization;
using System.Security.Claims;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Tenancy.Contracts;
using Platform.Modules.Vendors.Contracts;
using Platform.UI;
using Platform.Web.Components.Pages.Console;
using Platform.Web.PlatformHost;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// <c>/platform/vendors</c> driven through the component with bUnit on the test database (W-33): the console shows the
/// ownership check method and whether Wathq is set up, changes the method, and upholds an open dispute after a note and a
/// confirmation, which moves the company to the claimant.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class VendorOwnershipConsolePageTests(DatabaseFixture db) : IDisposable
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private readonly BunitContext _page = new();

    [Fact]
    public async Task The_console_changes_the_method_and_shows_that_wathq_is_not_set_up()
    {
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        await using var host = new ModuleHost(db.AppConnectionString);
        await using var scope = host.PlatformScope(admin);
        try
        {
            var page = Render(scope, admin);
            page.Find("[data-ownership-method='manual']");
            page.Find("[data-wathq-configured='false']");

            await page.Find("[data-method-select]").ChangeAsync(new() { Value = "wathq" });
            page.Find("[data-wathq-fallback]");
            await page.Find("[data-save-method]").ClickAsync(new());

            page.WaitForAssertion(() => page.Find("[data-ownership-method='wathq']"), RenderWait.Timeout);
            (await OwnershipRows.MethodAsync(db.OwnerConnectionString, Ct)).ShouldBe("wathq");
            (await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, admin, "vendor.ownership_method_changed", Ct)).ShouldHaveSingleItem();
        }
        finally
        {
            await OwnershipRows.SetMethodAsOwnerAsync(db.OwnerConnectionString, "manual", Ct);
        }
    }

    [Fact]
    public async Task Upholding_a_dispute_needs_a_note_and_moves_the_company_to_the_claimant()
    {
        var squatter = Guid.NewGuid().ToString();
        var crNumber = VendorRows.NewCrNumber();
        var companyId = await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Acme, squatter, crNumber, "Console Disputed Co", Ct);
        var claimant = Guid.NewGuid().ToString();
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        var accounts = new FakeVendorAccounts();
        await using var host = new ModuleHost(db.AppConnectionString, configure: s => s.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => accounts)));
        Guid disputeId;
        await using (var claim = host.ScopeFor(TestTenants.Acme, actingUserId: claimant))
        {
            disputeId = (await claim.ServiceProvider.GetRequiredService<ICrDisputes>().RaiseAsync(
                new CrDisputeRequest(crNumber, "The certificate names me.", VendorPrivacyNotice.CurrentVersion, VendorPrivacyNotice.English),
                "claimant@console.test", "Console Claimant", Ct)).Value;
        }

        await using var scope = host.PlatformScope(admin);
        var page = Render(scope, admin);
        page.WaitForElement($"[data-dispute='{disputeId}']", RenderWait.Timeout).TextContent.ShouldContain("Console Disputed Co");

        await page.Find($"[data-uphold='{disputeId}']").ClickAsync(new());
        await ConfirmAsync(page, "Move the company");
        page.WaitForAssertion(() => page.Find("[role=dialog]").TextContent.ShouldContain("Write what you checked, in up to 1,000 characters."), RenderWait.Timeout);

        await page.Find("[data-resolution-note]").ChangeAsync(new() { Value = "Called the claimant.\r\nChecked the certificate." });
        await ConfirmAsync(page, "Move the company");

        page.WaitForAssertion(() => page.FindAll($"[data-dispute='{disputeId}']").ShouldBeEmpty(), RenderWait.Timeout);
        (await OwnershipRows.DisputeAsync(db.OwnerConnectionString, disputeId, Ct)).ShouldNotBeNull().ResolutionNote.ShouldBe("Called the claimant.\nChecked the certificate.");
        (await OwnershipRows.VendorUsersAsync(db.OwnerConnectionString, companyId, Ct)).ShouldBe([(claimant, "vendor-admin")]);
        (await OwnershipRows.DisputeAsync(db.OwnerConnectionString, disputeId, Ct)).ShouldNotBeNull().Status.ShouldBe("upheld");
    }

    [Fact]
    public async Task Accepting_a_dispute_for_review_shows_it_under_review_and_a_failed_uphold_offers_a_retry()
    {
        var (companyId, disputeId, admin, accounts, host) = await DisputedAsync("Console Triage Co");
        await using var owned = host;
        await using var scope = host.PlatformScope(admin);
        var page = Render(scope, admin);
        page.WaitForElement($"[data-dispute-status='{disputeId}:open']", RenderWait.Timeout);
        page.Find($"[data-dispute-registrant='{disputeId}']").TextContent.ShouldContain("Squatting Person (self-declared)");

        await page.Find($"[data-accept='{disputeId}']").ClickAsync(new());
        await ConfirmAsync(page, "Accept for review");

        page.WaitForAssertion(() => page.Find($"[data-dispute-status='{disputeId}:under_review']"), RenderWait.Timeout);
        page.FindAll($"[data-accept='{disputeId}']").ShouldBeEmpty();

        // Keycloak refuses the claimant's role: the uphold stands and the console keeps a retry until it works.
        accounts.OnGrantRole = _ => throw new IdentityProviderException("Keycloak did not grant the vendor role.", new HttpRequestException("forced"));
        await page.Find($"[data-uphold='{disputeId}']").ClickAsync(new());
        await page.Find("[data-resolution-note]").ChangeAsync(new() { Value = "Called the claimant and checked the certificate." });
        await ConfirmAsync(page, "Move the company");
        page.WaitForAssertion(() => page.Find($"[data-idp-failure='{disputeId}']"), RenderWait.Timeout);

        accounts.OnGrantRole = null;
        await page.Find($"[data-idp-retry='{disputeId}']").ClickAsync(new());
        page.WaitForAssertion(() => page.FindAll($"[data-idp-failure='{disputeId}']").ShouldBeEmpty(), RenderWait.Timeout);
        (await OwnershipRows.VendorUsersAsync(db.OwnerConnectionString, companyId, Ct)).ShouldHaveSingleItem().Role.ShouldBe("vendor-admin");
    }

    [Fact]
    public async Task The_console_page_renders_in_arabic_without_raw_keys()
    {
        var (_, disputeId, admin, _, host) = await DisputedAsync("Arabic Console Co");
        await using var owned = host;
        await using var scope = host.PlatformScope(admin);

        var page = Render(scope, admin, "ar-SA");
        var status = page.WaitForElement($"[data-dispute-status='{disputeId}:open']", RenderWait.Timeout);

        var html = page.Markup;
        html.ShouldContain("طريقة التحقق من الملكية");
        html.ShouldContain("نزاعات الملكية");
        status.TextContent.ShouldContain("جديد، لم يُقبل بعد");
        page.Find($"[data-accept='{disputeId}']").TextContent.ShouldContain("قبول للمراجعة");
        html.ShouldNotContain("Console.Ownership.", Case.Sensitive, "no raw resource key");
        html.ShouldNotContain("Admin.Vendors.", Case.Sensitive, "no raw resource key");
    }

    public void Dispose() => _page.Dispose();

    private async Task<(Guid CompanyId, Guid DisputeId, string Admin, FakeVendorAccounts Accounts, ModuleHost Host)> DisputedAsync(string nameEn)
    {
        var squatter = Guid.NewGuid().ToString();
        var crNumber = VendorRows.NewCrNumber();
        var companyId = await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Acme, squatter, crNumber, nameEn, Ct);
        var accounts = new FakeVendorAccounts();
        accounts.Profiles[squatter] = new VendorAccountProfile("Squatting", "Person", "squatter@console.test", EmailVerified: true);
        var host = new ModuleHost(db.AppConnectionString, configure: s => s.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => accounts)));
        await using var claim = host.ScopeFor(TestTenants.Acme, actingUserId: Guid.NewGuid().ToString());
        var disputeId = (await claim.ServiceProvider.GetRequiredService<ICrDisputes>().RaiseAsync(
            new CrDisputeRequest(crNumber, "The certificate names me.", VendorPrivacyNotice.CurrentVersion, VendorPrivacyNotice.English),
            "claimant@console.test", "Console Claimant", Ct)).Value;
        return (companyId, disputeId, $"platform-admin-{Guid.NewGuid():N}", accounts, host);
    }

    private IRenderedComponent<VendorOwnership> Render(AsyncServiceScope scope, string admin, string culture = "en-US")
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        _page.Services.AddLocalization(o => o.ResourcesPath = "Resources");
        _page.Services.AddPlatformUI();
        _page.Services.AddSingleton(scope.ServiceProvider.GetRequiredService<ICrOwnershipAdministration>());
        _page.Services.AddSingleton(scope.ServiceProvider.GetRequiredService<ITenantCatalog>());
        _page.Services.AddSingleton(NullLogger<VendorOwnership>.Instance);
        _page.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = _page.AddAuthorization();
        auth.SetAuthorized(admin);
        auth.SetClaims(new Claim(IdentityClaims.Subject, admin));
        auth.SetPolicies(PlatformAuthentication.PolicyName);
        var page = _page.Render<VendorOwnership>();
        // The method section shows once the settings have loaded; the disputes and the failed updates load in later steps,
        // so a test waits for the rows it uses.
        page.WaitForAssertion(() => page.Find("[data-ownership-method]"), RenderWait.Timeout);
        return page;
    }

    private static Task ConfirmAsync(IRenderedComponent<VendorOwnership> page, string verb) =>
        page.Find("[role=dialog]").QuerySelectorAll("button").Single(b => b.TextContent.Trim() == verb).ClickAsync(new());
}
