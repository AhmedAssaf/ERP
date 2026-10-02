using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Bunit;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Vendors;
using Platform.Modules.Vendors.Contracts;
using Platform.Modules.Vendors.RateLimiting;
using Platform.UI;
using Platform.Web.Components.Pages.Vendor;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// W-37 and W-35 on the pages: a join refused by the join limit (<c>/vendor/join</c>, static server rendering, through the
/// web host) and a consent change refused by the company's consent limit (<c>/vendor/consent</c>, its dialog driven with
/// bUnit against the real ledger) show their own message in Arabic and English, never a raw resource key, and the
/// refused join asks Keycloak nothing.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed partial class VendorRateLimitPageTests(DatabaseFixture db) : IDisposable
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private readonly BunitContext _page = new();

    [Theory]
    [InlineData("en", "<html lang=\"en\" dir=\"ltr\">",
        "There have been too many requests to join Beta Industries in the last minute. Wait a minute and try again.")]
    [InlineData("ar", "<html lang=\"ar\" dir=\"rtl\">",
        "وصلت طلبات الانضمام إلى Beta Industries إلى حدّها خلال الدقيقة الماضية. انتظر دقيقة ثم حاول مرة أخرى.")]
    public async Task A_join_over_the_limit_reads_the_rate_limit_message_in_the_users_language(string locale, string htmlTag, string expected)
    {
        var userId = Guid.NewGuid().ToString();
        await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Acme, userId, VendorRows.NewCrNumber(), $"Rate Limited Joiner {locale}", Ct);
        var accounts = new FakeVendorAccounts { State = new(HoldsVendorRole: true, OrganizationAliases: [TestTenants.Acme.KeycloakOrgAlias]) };
        var user = new TestUser(userId, [TestTenants.Acme.KeycloakOrgAlias], locale, RealmRoles: [IdentityClaims.VendorRealmRole],
            Email: $"{userId}@vendor.test", EmailVerified: true);
        await using var factory = new PlatformWebFactory(db.AppConnectionString).WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => accounts))));
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("http://beta.localhost"), AllowAutoRedirect = false });

        // The user's five joins of the minute (the default) are used up.
        var limits = factory.Services.GetRequiredService<VendorRateLimits>();
        for (var i = 0; i < new VendorsOptions().JoinsPerUserPerMinute; i++)
        {
            limits.TryJoin(TestTenants.Beta.TenantId, userId).ShouldBeTrue();
        }

        using var page = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/vendor/join").As(user), Ct);
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        var form = JoinForm().Match(await page.Content.ReadAsStringAsync(Ct));
        form.Success.ShouldBeTrue("the page renders the join form");
        var fields = HiddenInputs().Matches(form.Value)
            .ToDictionary(m => WebUtility.HtmlDecode(m.Groups["name"].Value), m => WebUtility.HtmlDecode(m.Groups["value"].Value), StringComparer.Ordinal);
        using var post = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/vendor/join") { Content = new FormUrlEncodedContent(fields) }.As(user), Ct);

        post.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = WebUtility.HtmlDecode(await post.Content.ReadAsStringAsync(Ct));
        html.ShouldContain(htmlTag);
        html.ShouldContain("data-form-error");
        html.ShouldContain(expected);
        RawKey().IsMatch(html).ShouldBeFalse("no raw resource key");
        accounts.Steps.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("en-US", "Grant consent", "Your company has changed its consents too often in the last hour. Try again later.")]
    [InlineData("ar-SA", "منح الموافقة", "غيّرت شركتك موافقاتها مرات كثيرة خلال الساعة الماضية. حاول مرة أخرى لاحقًا.")]
    public async Task A_consent_change_over_the_limit_shows_the_rate_limit_message_in_the_users_language(string culture, string confirm, string expected)
    {
        var userId = Guid.NewGuid().ToString();
        var companyId = await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Acme, userId, VendorRows.NewCrNumber(), $"Consent Limit Page {culture}", Ct);
        var recipientId = await ConsentRows.AddRecipientAsync(db.OwnerConnectionString, $"Recipient of Consent Limit Page {culture}", Ct);
        await using var host = new ModuleHost(db.AppConnectionString, configure: s => s.Configure<VendorsOptions>(o => o.ConsentChangesPerCompanyPerHour = 1));
        await using var scope = host.ScopeFor(TestTenants.Acme, companyId, userId);
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(3)).DateTime);
        var ledger = scope.ServiceProvider.GetRequiredService<IConsentLedger>();
        (await ledger.GrantAsync(recipientId, ConsentScope.AwardRecords, today, today.AddYears(1), userId, Ct)).IsSuccess.ShouldBeTrue();
        var page = Render(ledger, userId, culture);

        await page.Find("[data-grant]").ClickAsync(new());
        await page.Find("[role=dialog] select[data-consent-recipient]").ChangeAsync(new() { Value = recipientId.ToString() });
        await page.Find("[role=dialog] select[data-consent-scope]").ChangeAsync(new() { Value = "po_records" });
        await page.Find("[role=dialog] input[data-consent-from]").ChangeAsync(new() { Value = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });
        await page.Find("[role=dialog] input[data-consent-to]").ChangeAsync(new() { Value = today.AddMonths(3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });
        await page.Find("[role=dialog]").QuerySelectorAll("button").Single(b => b.TextContent.Trim() == confirm).ClickAsync(new());

        page.WaitForAssertion(() => page.Find("[role=dialog] [role=alert]").TextContent.Trim().ShouldBe(expected), RenderWait.Timeout);
        RawKey().IsMatch(page.Markup).ShouldBeFalse("no raw resource key");
        (await ConsentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).Count.ShouldBe(1);
    }

    public void Dispose() => _page.Dispose();

    private IRenderedComponent<VendorConsent> Render(IConsentLedger ledger, string userId, string culture)
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        _page.Services.AddLocalization(o => o.ResourcesPath = "Resources");
        _page.Services.AddPlatformUI();
        _page.Services.AddSingleton(ledger);
        _page.Services.AddSingleton(TimeProvider.System);
        _page.Services.AddSingleton(NullLogger<VendorConsent>.Instance);
        _page.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = _page.AddAuthorization();
        auth.SetAuthorized(userId);
        auth.SetClaims(new Claim(IdentityClaims.Subject, userId));
        auth.SetPolicies(VendorPolicies.Vendor);
        var page = _page.Render<VendorConsent>();
        page.WaitForAssertion(() => page.Find("[data-grant]").HasAttribute("disabled").ShouldBeFalse(), RenderWait.Timeout);
        return page;
    }

    [GeneratedRegex("<form\\b[^>]*data-vendor-join-form[^>]*>.*?</form>", RegexOptions.Singleline)]
    private static partial Regex JoinForm();

    [GeneratedRegex("<input\\b(?=[^>]*type=\"hidden\")(?=[^>]*name=\"(?<name>[^\"]*)\")(?=[^>]*value=\"(?<value>[^\"]*)\")[^>]*>")]
    private static partial Regex HiddenInputs();

    // A shared resource key rendered as itself, as a missing key would render.
    [GeneratedRegex(@"\b(Vendor|Admin)\.[A-Z][A-Za-z]+\.[A-Za-z.]+")]
    private static partial Regex RawKey();
}
