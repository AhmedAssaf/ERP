using System.Globalization;
using System.Security.Claims;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
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
/// company as approved; the action is refused when the officer lost the role since the page opened.
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
        await using var host = new ModuleHost(db.AppConnectionString);
        await using var scope = host.ScopeFor(TestTenants.Acme, actingUserId: officer);
        var page = Render(scope, companyId, officer);
        page.Find($"[data-vendor-status='{companyId}:pending']");

        await page.Find("[data-approve]").ClickAsync(new());
        await ConfirmAsync(page);

        page.WaitForAssertion(() => page.Find($"[data-vendor-status='{companyId}:approved']"));
        page.FindAll("[data-approve]").ShouldBeEmpty();
        (await VendorRows.RelationshipsAsync(db.OwnerConnectionString, companyId, Ct))[TestTenants.Acme.TenantId].ShouldBe("approved");
        (await VendorRows.AuditsAsync(db.OwnerConnectionString, TestTenants.Acme.TenantId, officer, "vendor.approved", Ct)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task An_officer_who_lost_the_role_since_the_page_opened_is_refused()
    {
        var (companyId, officer) = await PendingVendorAsync("Dialog Refused Company");
        await using var host = new ModuleHost(db.AppConnectionString);
        await using var scope = host.ScopeFor(TestTenants.Acme, actingUserId: officer);
        var page = Render(scope, companyId, officer);
        await MemberRows.OverwriteRolesAsync(db.AppConnectionString, TestTenants.Acme.TenantId, officer, [TenantRoles.TechnicalEvaluator], Ct);

        await page.Find("[data-approve]").ClickAsync(new());
        await ConfirmAsync(page);

        page.WaitForAssertion(() => page.Find("[role=dialog] [role=alert]").TextContent.ShouldBe("Only a contracts officer or a tenant administrator can approve a vendor."));
        (await VendorRows.RelationshipsAsync(db.OwnerConnectionString, companyId, Ct))[TestTenants.Acme.TenantId].ShouldBe("pending");
    }

    public void Dispose() => _page.Dispose();

    private IRenderedComponent<VendorDetails> Render(AsyncServiceScope scope, Guid companyId, string officer)
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
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
        page.WaitForAssertion(() => page.Find($"[data-vendor-card='{companyId}']"));
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
