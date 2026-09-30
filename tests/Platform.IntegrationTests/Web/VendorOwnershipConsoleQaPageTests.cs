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
/// QA pass on PR #5 (W-33, docs/09 W-33 acceptance, ADR-0013 point 5): the console's retry of an uphold's identity
/// provider update tells the platform admin which of the three outcomes it had (<see cref="CrDisputeRetry.Updated"/>,
/// <see cref="CrDisputeRetry.StillFailing"/>, <see cref="CrDisputeRetry.Superseded"/>) in Arabic and English, and the
/// section listing failed updates renders in Arabic with its failed steps; the uphold and reject dialogs warn against
/// national ID or iqama numbers in the note (PDPL) in both languages. Driven through the component with bUnit on the test
/// database; the uphold itself runs through the service so each retry test is about the retry.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class VendorOwnershipConsoleQaPageTests(DatabaseFixture db) : IDisposable
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private static readonly string[] ToastTones = ["border-success", "border-danger", "border-action"];

    private readonly BunitContext _page = new();

    [Theory]
    [InlineData("en-US", "Faisal Al-Harbi", "The sign-in service now has the change for Faisal Al-Harbi.")]
    [InlineData("ar-SA", "فيصل الحربي", "أخذت خدمة تسجيل الدخول التغيير الخاص بـ فيصل الحربي.")]
    public async Task A_retry_that_succeeds_tells_the_admin_the_sign_in_service_has_the_change(string culture, string claimantName, string expected)
    {
        var accounts = new FakeVendorAccounts { OnGrantRole = _ => throw Refused() };
        await using var host = Host(accounts);
        var admin = Admin();
        var (_, _, crNumber) = await VendorAsync("Al Waha Trading");
        var disputeId = await RaiseAsync(host, Guid.NewGuid().ToString(), crNumber, claimantName);
        (await UpholdAsync(host, disputeId, admin)).IdentityProviderUpdated.ShouldBeFalse();
        accounts.OnGrantRole = null;
        await using var scope = host.PlatformScope(admin);
        var page = Render(scope, admin, culture);

        await page.Find($"[data-idp-retry='{disputeId}']").ClickAsync(new());

        page.WaitForAssertion(() => Toast(page).ShouldBe((expected, "border-success")));
        page.FindAll($"[data-idp-failure='{disputeId}']").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("en-US", "The sign-in service still did not take the change. Try again later, or change it in Keycloak.")]
    [InlineData("ar-SA", "لم تأخذ خدمة تسجيل الدخول التغيير بعد. حاول لاحقًا، أو غيّره في Keycloak.")]
    public async Task A_retry_that_still_fails_says_so_and_keeps_the_dispute_listed(string culture, string expected)
    {
        var accounts = new FakeVendorAccounts { OnGrantRole = _ => throw Refused() };
        await using var host = Host(accounts);
        var admin = Admin();
        var (_, _, crNumber) = await VendorAsync("Najd Supplies");
        var disputeId = await RaiseAsync(host, Guid.NewGuid().ToString(), crNumber, "Noura Al-Qahtani");
        (await UpholdAsync(host, disputeId, admin)).IdentityProviderUpdated.ShouldBeFalse();
        await using var scope = host.PlatformScope(admin);
        var page = Render(scope, admin, culture);

        await page.Find($"[data-idp-retry='{disputeId}']").ClickAsync(new());

        page.WaitForAssertion(() => Toast(page).ShouldBe((expected, "border-danger")));
        page.Find($"[data-idp-failure='{disputeId}']").QuerySelector("[data-idp-failed-steps]").ShouldNotBeNull().TextContent.ShouldContain("role:grant");
    }

    [Theory]
    [InlineData("en-US", "Khalid Al-Otaibi", "Khalid Al-Otaibi was given no access: the company no longer belongs to them, because a later request moved it.")]
    [InlineData("ar-SA", "خالد العتيبي", "لم يُمنح خالد العتيبي أي وصول: لم تعد المنشأة تابعة له، لأن طلبًا لاحقًا نقلها.")]
    public async Task A_retry_after_a_later_uphold_moved_the_company_says_it_was_superseded_and_leaves_the_list(string culture, string claimantName, string expected)
    {
        var accounts = new FakeVendorAccounts { FailRevoke = true };
        await using var host = Host(accounts);
        var admin = Admin();
        var (_, squatter, crNumber) = await VendorAsync("Eastern Province Logistics");
        accounts.Memberships[(squatter, TestTenants.Acme.KeycloakOrgAlias)] = true;
        var first = await RaiseAsync(host, Guid.NewGuid().ToString(), crNumber, claimantName);
        (await UpholdAsync(host, first, admin)).IdentityProviderUpdated.ShouldBeFalse("the squatter's access was not taken back");
        accounts.FailRevoke = false;
        (await UpholdAsync(host, await RaiseAsync(host, Guid.NewGuid().ToString(), crNumber, "Later Claimant"), admin)).IdentityProviderUpdated.ShouldBeTrue();
        await using var scope = host.PlatformScope(admin);
        var page = Render(scope, admin, culture);

        await page.Find($"[data-idp-retry='{first}']").ClickAsync(new());

        page.WaitForAssertion(() =>
        {
            var (text, tone) = Toast(page);
            text.ShouldStartWith(expected);
            tone.ShouldBe("border-action", "superseded is information, not a failure");
        });
        page.FindAll($"[data-idp-failure='{first}']").ShouldBeEmpty();
    }

    [Fact]
    public async Task The_failed_update_section_renders_in_arabic_with_its_failed_steps_and_no_raw_keys()
    {
        var accounts = new FakeVendorAccounts { OnGrantRole = _ => throw Refused() };
        await using var host = Host(accounts);
        var admin = Admin();
        var (_, _, crNumber) = await VendorAsync("Hijaz Contracting");
        var disputeId = await RaiseAsync(host, Guid.NewGuid().ToString(), crNumber, "ريم الشهري");
        (await UpholdAsync(host, disputeId, admin)).IdentityProviderUpdated.ShouldBeFalse();
        await using var scope = host.PlatformScope(admin);

        var page = Render(scope, admin, "ar-SA");

        var section = page.Find("[data-idp-failures]");
        section.TextContent.ShouldContain("لم تُحدَّث خدمة تسجيل الدخول");
        section.TextContent.ShouldContain("نُقلت إلى ريم الشهري في");
        section.QuerySelector("[data-idp-failed-steps]").ShouldNotBeNull().TextContent.ShouldContain("الخطوات التي تعذرت:");
        page.Find($"[data-idp-retry='{disputeId}']").TextContent.ShouldContain("حاول مرة أخرى");
        section.InnerHtml.ShouldNotContain("Console.Ownership.", Case.Sensitive, "no raw resource key");
    }

    [Theory]
    [InlineData("en-US", "data-uphold", "Do not write national ID or iqama numbers (personal data protection law).")]
    [InlineData("en-US", "data-reject", "Do not write national ID or iqama numbers (personal data protection law).")]
    [InlineData("ar-SA", "data-uphold", "لا تكتب أرقام الهوية الوطنية أو الإقامة (نظام حماية البيانات الشخصية).")]
    [InlineData("ar-SA", "data-reject", "لا تكتب أرقام الهوية الوطنية أو الإقامة (نظام حماية البيانات الشخصية).")]
    public async Task The_resolution_note_warns_against_national_id_or_iqama_numbers_in_the_admins_language(string culture, string action, string expected)
    {
        await using var host = Host(new FakeVendorAccounts());
        var admin = Admin();
        var (_, _, crNumber) = await VendorAsync("Riyadh Office Furniture");
        var disputeId = await RaiseAsync(host, Guid.NewGuid().ToString(), crNumber, "Lama Al-Subaie");
        await using var scope = host.PlatformScope(admin);
        var page = Render(scope, admin, culture);

        await page.Find($"[{action}='{disputeId}']").ClickAsync(new());

        page.WaitForAssertion(() => page.Find("[role=dialog]").TextContent.ShouldContain(expected));
    }

    public void Dispose() => _page.Dispose();

    private static IdentityProviderException Refused() =>
        new("Keycloak did not grant the vendor role.", new HttpRequestException("forced"));

    private static string Admin() => $"platform-admin-{Guid.NewGuid():N}";

    private static (string Text, string Tone) Toast(IRenderedComponent<VendorOwnership> page)
    {
        var toast = page.FindAll("[data-toast]").ShouldHaveSingleItem();
        var tone = ToastTones.Single(c => toast.ClassList.Contains(c));
        return (toast.QuerySelector("p").ShouldNotBeNull().TextContent.Trim(), tone);
    }

    private ModuleHost Host(FakeVendorAccounts accounts) =>
        new(db.AppConnectionString, configure: s => s.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => accounts)));

    private async Task<(Guid CompanyId, string Squatter, string CrNumber)> VendorAsync(string nameEn)
    {
        var squatter = Guid.NewGuid().ToString();
        var crNumber = VendorRows.NewCrNumber();
        var companyId = await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Acme, squatter, crNumber, nameEn, Ct);
        return (companyId, squatter, crNumber);
    }

    private static async Task<Guid> RaiseAsync(ModuleHost host, string claimant, string crNumber, string claimantName)
    {
        await using var scope = host.ScopeFor(TestTenants.Acme, actingUserId: claimant);
        var raised = await scope.ServiceProvider.GetRequiredService<ICrDisputes>().RaiseAsync(
            new CrDisputeRequest(crNumber, "Our CR certificate names our managing partner; another person registered the company.",
                VendorPrivacyNotice.CurrentVersion, VendorPrivacyNotice.English),
            $"{claimant}@claimant.test", claimantName, Ct);
        raised.IsSuccess.ShouldBeTrue(raised.Error?.Message);
        return raised.Value;
    }

    private static async Task<CrDisputeUpheld> UpholdAsync(ModuleHost host, Guid disputeId, string admin)
    {
        await using var scope = host.PlatformScope(admin);
        var upheld = await scope.ServiceProvider.GetRequiredService<ICrOwnershipAdministration>().UpholdAsync(
            disputeId, "Called the managing partner on the number in the CR certificate.", admin, Ct);
        upheld.IsSuccess.ShouldBeTrue(upheld.Error?.Message);
        return upheld.Value;
    }

    private IRenderedComponent<VendorOwnership> Render(AsyncServiceScope scope, string admin, string culture)
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
        page.WaitForAssertion(() => page.Find("[data-ownership-method]"));
        return page;
    }
}
