using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Vendors.Contracts;
using Platform.UI;
using Platform.Web.Components.Pages.Vendor;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// <c>/vendor/consent</c> (vendor plan task 6, F-64, V-12): the vendor admin's consent ledger with each grant's status
/// (active, not yet valid, expired or revoked), a grant dialog and a revoke dialog; linked from the vendor navigation;
/// right to left in Arabic without raw keys. The dialogs are driven with bUnit against the real ledger.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed partial class VendorConsentPageTests(DatabaseFixture db) : IDisposable
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private readonly BunitContext _page = new();

    [Theory]
    [InlineData("en", "dir=\"ltr\"")]
    [InlineData("ar", "dir=\"rtl\"")]
    public async Task A_vendor_opens_its_consent_ledger_in_its_language(string locale, string direction)
    {
        var (companyId, userId, recipientId) = await VendorWithRecipientAsync($"Consent Page {locale}");
        await GrantAsync(companyId, userId, recipientId);
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var client = factory.ClientFor("acme.localhost");

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/vendor/consent").As(Vendor(userId, locale)), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Ct));
        html.ShouldContain(direction);
        html.ShouldContain("data-consent-grant=");
        html.ShouldContain("data-consent-status=");
        html.ShouldContain(":active\"");
        html.ShouldContain(locale == "ar" ? "جهة مستلمة للاختبار" : $"Recipient of Consent Page {locale}");
        html.ShouldContain("href=\"vendor/consent\"");
        RawKey().IsMatch(html).ShouldBeFalse();
    }

    [Fact]
    public async Task Grant_and_revoke_run_through_the_page_dialogs()
    {
        var (companyId, userId, recipientId) = await VendorWithRecipientAsync("Dialog Consent Company");
        await using var host = new ModuleHost(db.AppConnectionString);
        await using var scope = host.ScopeFor(TestTenants.Acme, companyId, userId);
        var page = Render(scope, userId);
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(3)).DateTime);

        await page.Find("[data-grant]").ClickAsync(new());
        await page.Find("[role=dialog] select[data-consent-recipient]").ChangeAsync(new() { Value = recipientId.ToString() });
        await page.Find("[role=dialog] select[data-consent-scope]").ChangeAsync(new() { Value = "po_records" });
        await page.Find("[role=dialog] input[data-consent-from]").ChangeAsync(new() { Value = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });
        await page.Find("[role=dialog] input[data-consent-to]").ChangeAsync(new() { Value = today.AddMonths(3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });
        await ConfirmAsync(page, "Grant consent");

        page.WaitForAssertion(() => page.Find("[data-consent-grant]"), RenderWait.Timeout);
        var grant = (await ConsentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).ShouldHaveSingleItem();
        grant.Scope.ShouldBe("po_records");
        page.Find($"[data-consent-status='{grant.Id}:active']");

        await page.Find($"[data-revoke='{grant.Id}']").ClickAsync(new());
        await ConfirmAsync(page, "Revoke consent");

        page.WaitForAssertion(() => page.Find($"[data-consent-status='{grant.Id}:revoked']"), RenderWait.Timeout);
        page.FindAll($"[data-revoke='{grant.Id}']").ShouldBeEmpty();
        (await ConsentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_grant_with_the_end_before_the_start_shows_the_period_error_and_records_nothing()
    {
        var (companyId, userId, recipientId) = await VendorWithRecipientAsync("Wrong Period Company");
        await using var host = new ModuleHost(db.AppConnectionString);
        await using var scope = host.ScopeFor(TestTenants.Acme, companyId, userId);
        var page = Render(scope, userId);
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(3)).DateTime);

        await page.Find("[data-grant]").ClickAsync(new());
        await page.Find("[role=dialog] select[data-consent-recipient]").ChangeAsync(new() { Value = recipientId.ToString() });
        await page.Find("[role=dialog] select[data-consent-scope]").ChangeAsync(new() { Value = "award_records" });
        await page.Find("[role=dialog] input[data-consent-from]").ChangeAsync(new() { Value = today.AddDays(5).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });
        await page.Find("[role=dialog] input[data-consent-to]").ChangeAsync(new() { Value = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });
        await ConfirmAsync(page, "Grant consent");

        page.WaitForAssertion(() => page.Find("[role=dialog]").TextContent
            .ShouldContain("Choose a period that starts today or later and ends on or after its first day."), RenderWait.Timeout);
        (await ConsentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).ShouldBeEmpty();
    }

    public void Dispose() => _page.Dispose();

    private IRenderedComponent<VendorConsent> Render(AsyncServiceScope scope, string userId)
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        _page.Services.AddLocalization(o => o.ResourcesPath = "Resources");
        _page.Services.AddPlatformUI();
        _page.Services.AddSingleton(scope.ServiceProvider.GetRequiredService<IConsentLedger>());
        _page.Services.AddSingleton(TimeProvider.System);
        _page.Services.AddSingleton(NullLogger<VendorConsent>.Instance);
        _page.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = _page.AddAuthorization();
        auth.SetAuthorized(userId);
        auth.SetClaims(new Claim(IdentityClaims.Subject, userId));
        auth.SetPolicies(VendorPolicies.Vendor);
        var page = _page.Render<VendorConsent>();
        // Enabled once the recipients have loaded; the grants load in a later step, so a test waits for the rows it uses.
        page.WaitForAssertion(() => page.Find("[data-grant]").HasAttribute("disabled").ShouldBeFalse(), RenderWait.Timeout);
        return page;
    }

    private static Task ConfirmAsync(IRenderedComponent<VendorConsent> page, string text) =>
        page.Find("[role=dialog]").QuerySelectorAll("button").Single(b => b.TextContent.Trim() == text).ClickAsync(new());

    private async Task GrantAsync(Guid companyId, string userId, Guid recipientId)
    {
        await using var host = new ModuleHost(db.AppConnectionString);
        await using var scope = host.ScopeFor(TestTenants.Acme, companyId, userId);
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(3)).DateTime);
        (await scope.ServiceProvider.GetRequiredService<IConsentLedger>()
            .GrantAsync(recipientId, ConsentScope.AwardRecords, today, today.AddYears(1), userId, Ct)).IsSuccess.ShouldBeTrue();
    }

    private async Task<(Guid CompanyId, string UserId, Guid RecipientId)> VendorWithRecipientAsync(string nameEn)
    {
        var userId = Guid.NewGuid().ToString();
        var companyId = await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Acme, userId, VendorRows.NewCrNumber(), nameEn, Ct);
        var recipientId = await ConsentRows.AddRecipientAsync(db.OwnerConnectionString, $"Recipient of {nameEn}", Ct);
        return (companyId, userId, recipientId);
    }

    private static TestUser Vendor(string userId, string locale) =>
        new(userId, ["acme"], locale, RealmRoles: [IdentityClaims.VendorRealmRole], Email: $"{userId}@vendor.test", EmailVerified: true);

    // A shared resource key rendered as itself, as a missing key would render.
    [GeneratedRegex(@"\bVendor\.[A-Z][A-Za-z]+\.[A-Za-z.]+")]
    private static partial Regex RawKey();
}
