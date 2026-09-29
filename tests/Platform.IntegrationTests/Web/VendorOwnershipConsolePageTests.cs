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

            page.WaitForAssertion(() => page.Find("[data-ownership-method='wathq']"));
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
        page.Find($"[data-dispute='{disputeId}']").TextContent.ShouldContain("Console Disputed Co");

        await page.Find($"[data-uphold='{disputeId}']").ClickAsync(new());
        await ConfirmAsync(page, "Move the company");
        page.WaitForAssertion(() => page.Find("[role=dialog]").TextContent.ShouldContain("Write what you checked, in up to 1,000 characters."));

        await page.Find("[data-resolution-note]").ChangeAsync(new() { Value = "Called the claimant and checked the certificate." });
        await ConfirmAsync(page, "Move the company");

        page.WaitForAssertion(() => page.FindAll($"[data-dispute='{disputeId}']").ShouldBeEmpty());
        (await OwnershipRows.VendorUsersAsync(db.OwnerConnectionString, companyId, Ct)).ShouldBe([(claimant, "vendor-admin")]);
        (await OwnershipRows.DisputeAsync(db.OwnerConnectionString, disputeId, Ct)).ShouldNotBeNull().Status.ShouldBe("upheld");
    }

    public void Dispose() => _page.Dispose();

    private IRenderedComponent<VendorOwnership> Render(AsyncServiceScope scope, string admin)
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
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

    private static Task ConfirmAsync(IRenderedComponent<VendorOwnership> page, string verb) =>
        page.Find("[role=dialog]").QuerySelectorAll("button").Single(b => b.TextContent.Trim() == verb).ClickAsync(new());
}
