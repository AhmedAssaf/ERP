using System.Globalization;
using System.Security.Claims;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Vendors.Contracts;
using Platform.UI;
using Platform.Web.Components.Pages.Admin;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// <c>/admin/vendors/{id}</c> driven through the component with bUnit (vendor plan task 5, V-7, V-11): the Approve button
/// opens a dialog, confirming approves through the real directory on the test database, and the card then shows the
/// company as approved; the action is refused when the officer lost the role since the page opened. W-33: before a
/// company's first approval the dialog carries the ownership check (the registering person, what to check, a box and a
/// note), and approves only once the officer confirmed; a company verified already is approved without it.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class VendorApprovePageTests(DatabaseFixture db) : IDisposable
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private readonly BunitContext _page = new();

    [Fact]
    public async Task The_approve_dialog_approves_the_vendor_and_the_card_shows_it()
    {
        var (companyId, officer) = await PendingVendorAsync("Dialog Approved Company");
        await OwnershipRows.VerifyAsOwnerAsync(db.OwnerConnectionString, companyId, TestTenants.Beta.TenantId, Ct);
        await using var host = new ModuleHost(db.AppConnectionString);
        await using var scope = host.ScopeFor(TestTenants.Acme, actingUserId: officer);
        var page = Render(scope, companyId, officer);
        page.Find($"[data-vendor-status='{companyId}:pending']");

        await page.Find("[data-approve]").ClickAsync(new());
        // Verified already (at another tenant, which the page never names): no check, only what it was verified by.
        page.WaitForAssertion(() => page.Find("[data-ownership-verified='manual']"), RenderWait.Timeout);
        page.FindAll("[data-ownership-check]").ShouldBeEmpty();
        await ConfirmAsync(page);

        page.WaitForAssertion(() => page.Find($"[data-vendor-status='{companyId}:approved']"), RenderWait.Timeout);
        page.FindAll("[data-approve]").ShouldBeEmpty();
        (await VendorRows.RelationshipsAsync(db.OwnerConnectionString, companyId, Ct))[TestTenants.Acme.TenantId].ShouldBe("approved");
        (await VendorRows.AuditsAsync(db.OwnerConnectionString, TestTenants.Acme.TenantId, officer, "vendor.approved", Ct)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task The_dialog_shows_the_ownership_check_and_approves_once_the_officer_confirms_with_a_note()
    {
        var (companyId, officer) = await PendingVendorAsync("Dialog Ownership Company");
        var registrant = (await OwnershipRows.VendorUsersAsync(db.OwnerConnectionString, companyId, Ct)).Single().UserId;
        await VendorDocumentRows.InsertAsync(db.OwnerConnectionString, companyId, VendorDocumentTypes.CrCertificate, new DateOnly(2031, 1, 1), "clean", isCurrent: true, Ct);
        var accounts = new FakeVendorAccounts();
        accounts.Profiles[registrant] = new VendorAccountProfile("Huda", "Alharbi", "huda@ownership.test", EmailVerified: true);
        await using var host = new ModuleHost(db.AppConnectionString, configure: s => s.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => accounts)));
        await using var scope = host.ScopeFor(TestTenants.Acme, actingUserId: officer);
        var page = Render(scope, companyId, officer);

        await page.Find("[data-approve]").ClickAsync(new());

        page.WaitForAssertion(() => page.Find("[data-ownership-check]"), RenderWait.Timeout);
        page.Find("[data-registrant-name]").TextContent.ShouldContain("Huda Alharbi (self-declared)");
        page.Find("[data-registrant-email='verified']").TextContent.ShouldContain("huda@ownership.test");
        page.Find("[data-registrant-email='verified']").TextContent.ShouldContain("(email verified)");
        page.Find("[data-ownership-guidance]").TextContent.ShouldContain("authorisation letter");
        page.Find("[data-ownership-note]").TagName.ShouldBe("TEXTAREA");
        page.Find("[data-ownership-check]").TextContent.ShouldContain("Do not write national ID or iqama numbers");
        page.Find("[data-ownership-lookup='manual']").TextContent.ShouldContain("commercial registration certificate names the person");

        // Not confirmed: refused before anything is recorded.
        await ConfirmAsync(page);
        page.WaitForAssertion(() => page.Find("[role=dialog] [role=alert]").TextContent.ShouldBe("Tick the box to confirm that you checked the company's ownership."), RenderWait.Timeout);

        // Confirmed without a note: the note field says what is missing.
        await page.Find("[data-ownership-confirm]").ChangeAsync(new() { Value = true });
        await ConfirmAsync(page);
        page.WaitForAssertion(() => page.Find("[role=dialog]").TextContent.ShouldContain("Write what you checked, in up to 1,000 characters."), RenderWait.Timeout);
        (await VendorRows.RelationshipsAsync(db.OwnerConnectionString, companyId, Ct))[TestTenants.Acme.TenantId].ShouldBe("pending");

        await page.Find("[data-ownership-note]").ChangeAsync(new() { Value = "The certificate names Huda Alharbi as the owner.\r\nShe sent an authorisation letter." });
        await ConfirmAsync(page);

        page.WaitForAssertion(() => page.Find($"[data-vendor-status='{companyId}:approved']"), RenderWait.Timeout);
        var verification = (await OwnershipRows.VerificationAsync(db.OwnerConnectionString, companyId, Ct)).ShouldNotBeNull();
        verification.Method.ShouldBe("manual");
        verification.Note.ShouldBe("The certificate names Huda Alharbi as the owner.\nShe sent an authorisation letter.");
        verification.VerifiedBy.ShouldBe(officer);
    }

    [Fact]
    public async Task The_dialog_says_when_the_company_has_no_certificate_to_check()
    {
        var (companyId, officer) = await PendingVendorAsync("Dialog No Certificate Company");
        await using var host = new ModuleHost(db.AppConnectionString);
        await using var scope = host.ScopeFor(TestTenants.Acme, actingUserId: officer);
        var page = Render(scope, companyId, officer);

        await page.Find("[data-approve]").ClickAsync(new());

        page.WaitForAssertion(() => page.Find("[data-ownership-no-certificate]"), RenderWait.Timeout);
        // The identity provider is not wired in this host: the registrant is unknown, and the page says so.
        page.Find("[data-ownership-registrant]").TextContent.ShouldContain("The sign-in service did not answer");
        await page.Find("[data-ownership-confirm]").ChangeAsync(new() { Value = true });
        await page.Find("[data-ownership-note]").ChangeAsync(new() { Value = "Checked." });
        await ConfirmAsync(page);

        page.WaitForAssertion(() => page.Find("[role=dialog] [role=alert]").TextContent.ShouldStartWith("The company has no current commercial registration certificate"), RenderWait.Timeout);
        (await OwnershipRows.VerificationAsync(db.OwnerConnectionString, companyId, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task An_officer_who_lost_the_role_since_the_page_opened_is_refused()
    {
        var (companyId, officer) = await PendingVendorAsync("Dialog Refused Company");
        await OwnershipRows.VerifyAsOwnerAsync(db.OwnerConnectionString, companyId, TestTenants.Acme.TenantId, Ct);
        await using var host = new ModuleHost(db.AppConnectionString);
        await using var scope = host.ScopeFor(TestTenants.Acme, actingUserId: officer);
        var page = Render(scope, companyId, officer);
        await MemberRows.OverwriteRolesAsync(db.AppConnectionString, TestTenants.Acme.TenantId, officer, [TenantRoles.TechnicalEvaluator], Ct);

        await page.Find("[data-approve]").ClickAsync(new());
        page.WaitForAssertion(() => page.Find("[data-ownership-verified]"), RenderWait.Timeout);
        await ConfirmAsync(page);

        page.WaitForAssertion(() => page.Find("[role=dialog] [role=alert]").TextContent.ShouldBe("Only a contracts officer or a tenant administrator can approve a vendor."), RenderWait.Timeout);
        (await VendorRows.RelationshipsAsync(db.OwnerConnectionString, companyId, Ct))[TestTenants.Acme.TenantId].ShouldBe("pending");
    }

    [Fact]
    public async Task The_ownership_check_renders_in_arabic_without_raw_keys_and_labels_an_unverified_email()
    {
        var (companyId, officer) = await PendingVendorAsync("Arabic Dialog Company");
        var registrant = (await OwnershipRows.VendorUsersAsync(db.OwnerConnectionString, companyId, Ct)).Single().UserId;
        await VendorDocumentRows.InsertAsync(db.OwnerConnectionString, companyId, VendorDocumentTypes.CrCertificate, new DateOnly(2031, 1, 1), "clean", isCurrent: true, Ct);
        var accounts = new FakeVendorAccounts();
        accounts.Profiles[registrant] = new VendorAccountProfile("هدى", "الحربي", "huda@unverified.test", EmailVerified: false);
        await using var host = new ModuleHost(db.AppConnectionString, configure: s => s.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => accounts)));
        await using var scope = host.ScopeFor(TestTenants.Acme, actingUserId: officer);
        var page = Render(scope, companyId, officer, "ar-SA");

        await page.Find("[data-approve]").ClickAsync(new());

        page.WaitForAssertion(() => page.Find("[data-ownership-check]"), RenderWait.Timeout);
        var dialog = page.Find("[role=dialog]").TextContent;
        dialog.ShouldContain("التحقق من الملكية");
        dialog.ShouldContain("(كما أدخله بنفسه)");
        page.Find("[data-registrant-email='unverified']").TextContent.ShouldContain("(بريد غير موثَّق)");
        dialog.ShouldContain("خطاب تفويض");
        dialog.ShouldNotContain("Admin.Vendors.", Case.Sensitive, "no raw resource key");
    }

    public void Dispose() => _page.Dispose();

    private IRenderedComponent<VendorDetails> Render(AsyncServiceScope scope, Guid companyId, string officer, string culture = "en-US")
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        _page.Services.AddLocalization(o => o.ResourcesPath = "Resources");
        _page.Services.AddPlatformUI();
        _page.Services.AddSingleton(scope.ServiceProvider.GetRequiredService<IVendorDirectory>());
        _page.Services.AddSingleton(TimeProvider.System);
        _page.Services.AddSingleton(NullLogger<VendorDetails>.Instance);
        _page.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = _page.AddAuthorization();
        auth.SetAuthorized(officer);
        auth.SetClaims(new Claim(IdentityClaims.Subject, officer));
        auth.SetPolicies(VendorPolicies.VendorManager);
        var page = _page.Render<VendorDetails>(p => p.Add(x => x.CompanyId, companyId));
        page.WaitForAssertion(() => page.Find($"[data-vendor-card='{companyId}']"), RenderWait.Timeout);
        return page;
    }

    private static Task ConfirmAsync(IRenderedComponent<VendorDetails> page) =>
        page.Find("[role=dialog]").QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Approve vendor").ClickAsync(new());

    private async Task<(Guid CompanyId, string Officer)> PendingVendorAsync(string nameEn)
    {
        var companyId = await VendorRows.RegisterAsync(
            db.AppConnectionString, TestTenants.Acme, Guid.NewGuid().ToString(), VendorRows.NewCrNumber(), nameEn, Ct);
        var officer = $"officer-{Guid.NewGuid():N}";
        await MemberRows.InsertAsync(db.AppConnectionString, TestTenants.Acme.TenantId, officer, $"{officer}@acme.test", [TenantRoles.ContractsOfficer], "active", Ct);
        return (companyId, officer);
    }
}
