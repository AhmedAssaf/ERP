using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.IntegrationTests.Infrastructure;
using Platform.Migrator;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Vendors.Contracts;
using Platform.UI;
using Platform.Web.Components.Pages.Vendor;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// QA pass on <c>/vendor/consent</c> (vendor plan task 6, F-64, V-12, V-13), beside <see cref="VendorConsentPageTests"/>:
/// the grant and revoke dialogs in Arabic and English without raw keys, the development seed's recipient named in the
/// page's language, each status's own text in both languages, the grant dialog's first day at the Riyadh midnight boundary,
/// and what a signed-in user who is not the company's vendor admin sees. Rendered with bUnit against the real ledger, with
/// one fixed clock for the page and the ledger (09:00 in Riyadh on a day 60 days after the database's own date, so the
/// no-backdating trigger accepts every grant whatever day the suite runs; <see cref="DatabaseClock"/>).
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed partial class VendorConsentPageQaTests(DatabaseFixture db) : IAsyncLifetime
{
    private DateOnly D;

    private DateTimeOffset Now;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private readonly BunitContext _page = new();

    public async ValueTask InitializeAsync()
    {
        D = await DatabaseClock.FutureRiyadhDayAsync(db.OwnerConnectionString, Ct);
        Now = new DateTimeOffset(D.ToDateTime(new TimeOnly(9, 0)), TimeSpan.FromHours(3));
    }

    public ValueTask DisposeAsync()
    {
        _page.Dispose();
        return ValueTask.CompletedTask;
    }

    public static TheoryData<string, string, string, string> Languages => new()
    {
        { "en-US", "Give consent", "Test finance partner", "Revoke consent" },
        { "ar-SA", "منح موافقة", "شريك تمويل تجريبي", "سحب الموافقة" },
    };

    public static TheoryData<string, string, string> NotAdminLanguages => new()
    {
        { "en-US", "Grant consent", "Only your company's vendor administrator can change its consent." },
        { "ar-SA", "منح الموافقة", "لا يغيّر موافقات الشركة إلا مسؤول حسابها." },
    };

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task The_grant_dialog_offers_the_dev_seed_recipient_in_the_page_language_without_raw_keys(
        string culture, string grantTitle, string seedName, string revokeConfirm)
    {
        _ = revokeConfirm;
        await DevSeed.SeedConsentRecipientsAsync(db.OwnerConnectionString, Ct);
        var (companyId, userId) = await VendorAsync($"Seed Dialog {culture}");
        await using var host = Host(Now);
        await using var scope = host.ScopeFor(TestTenants.Acme, companyId, userId);
        var page = Render(scope, userId, culture, Now);

        await page.Find("[data-grant]").ClickAsync(new());

        var dialog = page.Find("[role=dialog]");
        dialog.QuerySelector("h2")!.TextContent.Trim().ShouldBe(grantTitle);
        dialog.QuerySelector($"select[data-consent-recipient] option[value='{DevSeed.TestRecipientId}']")!.TextContent.Trim().ShouldBe(seedName);
        RawKey().IsMatch(dialog.OuterHtml).ShouldBeFalse(dialog.OuterHtml);
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task The_revoke_dialog_names_the_recipient_and_confirms_in_the_page_language(
        string culture, string grantTitle, string seedName, string revokeConfirm)
    {
        _ = grantTitle;
        await DevSeed.SeedConsentRecipientsAsync(db.OwnerConnectionString, Ct);
        var (companyId, userId) = await VendorAsync($"Revoke Dialog {culture}");
        await using var host = Host(Now);
        var grantId = await GrantAsync(host, companyId, userId, DevSeed.TestRecipientId, D, D.AddYears(1));
        await using var scope = host.ScopeFor(TestTenants.Acme, companyId, userId);
        var page = Render(scope, userId, culture, Now);

        await page.WaitForElement($"[data-revoke='{grantId}']", RenderWait.Timeout).ClickAsync(new());
        var dialog = page.Find("[role=dialog]");
        dialog.QuerySelector("h2")!.TextContent.ShouldContain(seedName);
        RawKey().IsMatch(dialog.OuterHtml).ShouldBeFalse(dialog.OuterHtml);
        await ConfirmAsync(page, revokeConfirm);

        page.WaitForAssertion(() => page.Find($"[data-consent-status='{grantId}:revoked']"), RenderWait.Timeout);
        RawKey().IsMatch(page.Markup).ShouldBeFalse();
    }

    [Theory]
    [InlineData("en-US", "Active", "Starts later", "Expired", "Revoked")]
    [InlineData("ar-SA", "سارية", "تبدأ لاحقًا", "منتهية", "ملغاة")]
    public async Task Each_status_shows_its_own_text_in_the_page_language(
        string culture, string active, string notYetValid, string expired, string revoked)
    {
        var (companyId, userId) = await VendorAsync($"Four Statuses {culture}");
        var recipientId = await ConsentRows.AddRecipientAsync(db.OwnerConnectionString, $"Status Recipient {culture}", Ct);
        Guid expiredId;
        await using (var earlier = Host(Now.AddDays(-40)))
        {
            expiredId = await GrantAsync(earlier, companyId, userId, recipientId, D.AddDays(-40), D.AddDays(-20));
        }

        await using var host = Host(Now);
        var activeId = await GrantAsync(host, companyId, userId, recipientId, D, D.AddDays(30));
        var laterId = await GrantAsync(host, companyId, userId, recipientId, D.AddDays(5), D.AddDays(10));
        var revokedId = await GrantAsync(host, companyId, userId, recipientId, D, D.AddDays(30));
        await using (var revoking = host.ScopeFor(TestTenants.Acme, companyId, userId))
        {
            (await revoking.ServiceProvider.GetRequiredService<IConsentLedger>().RevokeAsync(revokedId, userId, Ct)).IsSuccess.ShouldBeTrue();
        }

        await using var scope = host.ScopeFor(TestTenants.Acme, companyId, userId);
        var page = Render(scope, userId, culture, Now);

        new[]
        {
            page.WaitForElement($"[data-consent-status='{activeId}:active']", RenderWait.Timeout).TextContent.Trim(),
            page.WaitForElement($"[data-consent-status='{laterId}:not_yet_valid']", RenderWait.Timeout).TextContent.Trim(),
            page.WaitForElement($"[data-consent-status='{expiredId}:expired']", RenderWait.Timeout).TextContent.Trim(),
            page.WaitForElement($"[data-consent-status='{revokedId}:revoked']", RenderWait.Timeout).TextContent.Trim(),
        }.ShouldBe([active, notYetValid, expired, revoked]);
    }

    [Fact]
    public async Task Only_a_grant_that_is_active_or_starts_later_offers_revoke()
    {
        var (companyId, userId) = await VendorAsync("Revocable Rows Company");
        var recipientId = await ConsentRows.AddRecipientAsync(db.OwnerConnectionString, "Revocable Rows Recipient", Ct);
        Guid expiredId;
        await using (var earlier = Host(Now.AddDays(-40)))
        {
            expiredId = await GrantAsync(earlier, companyId, userId, recipientId, D.AddDays(-40), D.AddDays(-20));
        }

        await using var host = Host(Now);
        var activeId = await GrantAsync(host, companyId, userId, recipientId, D, D.AddDays(30));
        var laterId = await GrantAsync(host, companyId, userId, recipientId, D.AddDays(5), D.AddDays(10));
        await using var scope = host.ScopeFor(TestTenants.Acme, companyId, userId);
        var page = Render(scope, userId, "en-US", Now);

        page.WaitForAssertion(
            () => page.FindAll("[data-revoke]").Select(b => b.GetAttribute("data-revoke") ?? string.Empty).Order().ShouldBe(new[] { activeId.ToString(), laterId.ToString() }.Order()),
            RenderWait.Timeout);
        page.FindAll($"[data-revoke='{expiredId}']").ShouldBeEmpty();
    }

    [Fact]
    public async Task The_grant_dialog_starts_on_today_in_Riyadh_just_after_midnight_there_while_UTC_is_still_yesterday()
    {
        var (companyId, userId) = await VendorAsync("Midnight Dialog Company");
        await ConsentRows.AddRecipientAsync(db.OwnerConnectionString, "Midnight Dialog Recipient", Ct);
        // 00:30 on D in Riyadh is 21:30 on D - 1 in UTC.
        var justAfterMidnight = DatabaseClock.Utc(D.AddDays(-1), new TimeOnly(21, 30));
        await using var host = Host(justAfterMidnight);
        await using var scope = host.ScopeFor(TestTenants.Acme, companyId, userId);
        var page = Render(scope, userId, "en-US", justAfterMidnight);

        await page.Find("[data-grant]").ClickAsync(new());

        page.Find("[role=dialog] input[data-consent-from]").GetAttribute("value").ShouldBe(D.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        page.Find("[role=dialog] input[data-consent-to]").GetAttribute("value").ShouldBe(D.AddYears(1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [MemberData(nameof(NotAdminLanguages))]
    public async Task A_signed_in_user_who_is_not_the_companys_vendor_admin_sees_the_admin_only_error_on_grant_and_nothing_is_recorded(
        string culture, string grantConfirm, string notAdmin)
    {
        var (companyId, _) = await VendorAsync($"Admin Only Grant {culture}");
        var (_, strangerId) = await VendorAsync($"Stranger Grant {culture}");
        var recipientId = await ConsentRows.AddRecipientAsync(db.OwnerConnectionString, $"Admin Only Recipient {culture}", Ct);
        await using var host = Host(Now);
        await using var scope = host.ScopeFor(TestTenants.Acme, companyId, strangerId);
        var page = Render(scope, strangerId, culture, Now);

        await page.Find("[data-grant]").ClickAsync(new());
        await page.Find("[role=dialog] select[data-consent-recipient]").ChangeAsync(new() { Value = recipientId.ToString() });
        await page.Find("[role=dialog] select[data-consent-scope]").ChangeAsync(new() { Value = "award_records" });
        await ConfirmAsync(page, grantConfirm);

        page.WaitForAssertion(() => page.Find("[role=dialog] [role=alert]").TextContent.Trim().ShouldBe(notAdmin), RenderWait.Timeout);
        (await ConsentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("en-US", "Revoke consent", "Only your company's vendor administrator can change its consent.")]
    [InlineData("ar-SA", "سحب الموافقة", "لا يغيّر موافقات الشركة إلا مسؤول حسابها.")]
    public async Task A_signed_in_user_who_is_not_the_companys_vendor_admin_sees_the_admin_only_error_on_revoke_and_the_grant_stays(
        string culture, string revokeConfirm, string notAdmin)
    {
        var (companyId, adminId) = await VendorAsync($"Admin Only Revoke {culture}");
        var (_, strangerId) = await VendorAsync($"Stranger Revoke {culture}");
        var recipientId = await ConsentRows.AddRecipientAsync(db.OwnerConnectionString, $"Admin Only Revoke Recipient {culture}", Ct);
        await using var host = Host(Now);
        var grantId = await GrantAsync(host, companyId, adminId, recipientId, D, D.AddDays(30));
        await using var scope = host.ScopeFor(TestTenants.Acme, companyId, strangerId);
        var page = Render(scope, strangerId, culture, Now);

        await page.WaitForElement($"[data-revoke='{grantId}']", RenderWait.Timeout).ClickAsync(new());
        await ConfirmAsync(page, revokeConfirm);

        page.WaitForAssertion(() => page.Find("[role=dialog] [role=alert]").TextContent.Trim().ShouldBe(notAdmin), RenderWait.Timeout);
        page.Find($"[data-consent-status='{grantId}:active']");
        (await ConsentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).ShouldHaveSingleItem().Kind.ShouldBe("grant");
    }

    private IRenderedComponent<VendorConsent> Render(AsyncServiceScope scope, string userId, string culture, DateTimeOffset now)
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        _page.Services.AddLocalization(o => o.ResourcesPath = "Resources");
        _page.Services.AddPlatformUI();
        _page.Services.AddSingleton(scope.ServiceProvider.GetRequiredService<IConsentLedger>());
        _page.Services.AddSingleton<TimeProvider>(new FixedClock(now));
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

    private ModuleHost Host(DateTimeOffset now) => new(db.AppConnectionString, clock: new FixedClock(now));

    private static async Task<Guid> GrantAsync(ModuleHost host, Guid companyId, string userId, Guid recipientId, DateOnly from, DateOnly to)
    {
        await using var scope = host.ScopeFor(TestTenants.Acme, companyId, userId);
        var granted = await scope.ServiceProvider.GetRequiredService<IConsentLedger>().GrantAsync(recipientId, ConsentScope.AwardRecords, from, to, userId, Ct);
        return granted.IsSuccess ? granted.Value : throw new ShouldAssertException(granted.Error.Message);
    }

    private async Task<(Guid CompanyId, string UserId)> VendorAsync(string nameEn)
    {
        var userId = Guid.NewGuid().ToString();
        var companyId = await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Acme, userId, VendorRows.NewCrNumber(), nameEn, Ct);
        return (companyId, userId);
    }

    // A shared resource key rendered as itself, as a missing key would render (Vendor.*, Dialog.*, Common.*).
    [GeneratedRegex(@"\b(?:Vendor|Dialog|Common)\.[A-Z][A-Za-z]+(?:\.[A-Za-z]+)*")]
    private static partial Regex RawKey();

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }
}
