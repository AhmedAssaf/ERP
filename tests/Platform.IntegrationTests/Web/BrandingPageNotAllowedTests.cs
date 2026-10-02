using System.Globalization;
using System.Security.Claims;
using Bunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Tenancy.Contracts;
using Platform.UI;
using Platform.Web.Components.Pages.Admin;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// Review of the vendor slice: when the database refuses the acting user of <c>/admin/branding</c> (here a tenant admin
/// who is also a vendor user, tenancy migration 0008), the details form says the user may not change the branding, in
/// the page's language, not the generic error. Rendered with bUnit against the real branding service.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class BrandingPageNotAllowedTests(DatabaseFixture db, MinioFixture minio) : IClassFixture<MinioFixture>, IDisposable
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private readonly BunitContext _page = new();

    [Theory]
    [InlineData("en-US", "Only an active tenant admin of this organization can change its branding, so this change was not saved.")]
    [InlineData("ar-SA", "لا يغيّر هوية الجهة إلا مسؤول نشط عنها، لذا لم يُحفظ هذا التغيير.")]
    public async Task A_save_the_database_refuses_shows_the_not_allowed_message(string culture, string message)
    {
        var tenant = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        var dualUser = $"dual-page-{Guid.NewGuid():N}";
        await VendorRows.RegisterAsync(db.AppConnectionString, tenant, dualUser, VendorRows.NewCrNumber(), $"Dual Page Company {culture}", Ct);
        await MemberRows.EnsureActiveAdminAsync(db.OwnerConnectionString, tenant.TenantId, dualUser, Ct);
        await using var host = new ModuleHost(db.AppConnectionString, objectStorage: new ConfigurationBuilder().AddInMemoryCollection(minio.Settings).Build());
        await using var scope = host.ScopeFor(tenant, actingUserId: dualUser);

        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        _page.Services.AddLocalization(o => o.ResourcesPath = "Resources");
        _page.Services.AddPlatformUI();
        _page.Services.AddSingleton(scope.ServiceProvider.GetRequiredService<IBrandingService>());
        _page.Services.AddSingleton(scope.ServiceProvider.GetRequiredService<IMemberDirectory>());
        _page.Services.AddSingleton(NullLogger<BrandingPage>.Instance);
        _page.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = _page.AddAuthorization();
        auth.SetAuthorized(dualUser);
        auth.SetClaims(new Claim(IdentityClaims.Subject, dualUser));
        auth.SetPolicies(TenantPolicies.TenantAdmin);
        var page = _page.Render<BrandingPage>();
        // The form renders before the current branding loads; a submit before that would send an empty name.
        page.WaitForAssertion(
            () => page.Find("[data-branding-form] input[maxlength='100']").GetAttribute("value").ShouldBe(tenant.Branding.PortalName),
            RenderWait.Timeout);

        await page.Find("[data-branding-form]").SubmitAsync();

        page.WaitForAssertion(() => page.Find("[data-branding-form] [role=alert]").TextContent.Trim().ShouldBe(message), RenderWait.Timeout);
        (await TenantRows.BrandingAsync(db.AppConnectionString, tenant, Ct)).ShouldBe(tenant.Branding);
    }

    public void Dispose() => _page.Dispose();
}
