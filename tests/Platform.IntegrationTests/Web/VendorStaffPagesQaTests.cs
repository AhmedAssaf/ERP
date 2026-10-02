using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Vendors.Contracts;
using Platform.Shared.Tenancy;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// QA pass on the pages of vendor plan task 5 (F-10 as narrowed, V-7, V-11): the rest of the role matrix for
/// <c>/admin/vendors</c>, <c>/admin/vendors/{id}</c> and <c>/vendor/join</c> (anonymous, staff of another tenant), Arabic
/// right-to-left rendering of the join page in each state and of a card with documents, the card's expiry badge at the
/// Riyadh midnight boundary, and which principals the home redirect leaves alone.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed partial class VendorStaffPagesQaTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> GuardedPaths => new("/admin/vendors", "/admin/vendors/5b1f0c2e-7a44-4d7e-9a3c-2f6d8e1b9c07", "/vendor/join");

    [Theory]
    [MemberData(nameof(GuardedPaths))]
    public async Task An_anonymous_request_for_a_vendor_staff_page_or_the_join_page_is_challenged(string path)
    {
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var client = factory.ClientFor("acme.localhost");

        using var response = await AdminRequests.GetAsync(client, path, user: null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(TenantRoles.ContractsOfficer)]
    [InlineData(TenantRoles.TenantAdmin)]
    public async Task Staff_of_another_tenant_are_forbidden_on_this_tenants_vendor_pages(string role)
    {
        var (companyId, _) = await VendorAsync("Acme Only Supplies", TestTenants.Acme);
        var betaStaff = await AdminRequests.MemberAsync(db, TestTenants.Beta, [role], "en", Ct);
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var acme = factory.ClientFor("acme.localhost");

        foreach (var path in new[] { "/admin/vendors", $"/admin/vendors/{companyId}" })
        {
            using var response = await AdminRequests.GetAsync(acme, path, betaStaff, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, path);
        }
    }

    [Fact]
    public async Task The_join_page_renders_right_to_left_in_arabic_in_every_state_without_raw_keys()
    {
        var (_, userId) = await VendorAsync("Arabic Join Company", TestTenants.Acme);
        var vendor = Vendor(userId, "ar");
        var accounts = new FakeVendorAccounts { State = new(HoldsVendorRole: true, OrganizationAliases: ["acme"]) };
        await using var factory = new PlatformWebFactory(db.AppConnectionString).WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => accounts))));
        using var beta = factory.CreateClient(new() { BaseAddress = new Uri("http://beta.localhost"), AllowAutoRedirect = false });
        using var acme = factory.CreateClient(new() { BaseAddress = new Uri("http://acme.localhost"), AllowAutoRedirect = false });

        using var offer = await beta.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/vendor/join").As(vendor), Ct);
        offer.StatusCode.ShouldBe(HttpStatusCode.OK);
        var offerHtml = await offer.Content.ReadAsStringAsync(Ct);
        AssertArabic(offerHtml, "offer");
        WebUtility.HtmlDecode(offerHtml).ShouldContain("العمل مع Beta Industries");

        using var joined = await PostJoinAsync(beta, vendor, offerHtml);
        var joinedHtml = await joined.Content.ReadAsStringAsync(Ct);
        joinedHtml.ShouldContain("data-vendor-joined");
        AssertArabic(joinedHtml, "joined");

        using var already = await acme.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/vendor/join").As(vendor), Ct);
        var alreadyHtml = await already.Content.ReadAsStringAsync(Ct);
        alreadyHtml.ShouldContain("data-vendor-already-joined");
        AssertArabic(alreadyHtml, "already");
    }

    [Fact]
    public async Task The_vendor_card_with_documents_renders_in_arabic_with_gregorian_dates_and_without_raw_keys()
    {
        var (companyId, _) = await VendorAsync("Arabic Card Company", TestTenants.Acme);
        var validUntil = await DatabaseClock.ValidUntilAsync(db.OwnerConnectionString, Ct);
        await VendorDocumentRows.InsertAsync(db.OwnerConnectionString, companyId, VendorDocumentTypes.CrCertificate, validUntil, "clean", isCurrent: true, Ct, createdAt: DateTimeOffset.UtcNow);
        await VendorDocumentRows.InsertAsync(db.OwnerConnectionString, companyId, VendorDocumentTypes.CrCertificate, new DateOnly(2025, 1, 31), "clean", isCurrent: false, Ct, createdAt: DateTimeOffset.UtcNow.AddDays(-400));
        await VendorDocumentRows.InsertAsync(db.OwnerConnectionString, companyId, VendorDocumentTypes.VatCertificate, new DateOnly(2020, 6, 30), "clean", isCurrent: true, Ct);
        var officer = await AdminRequests.MemberAsync(db, TestTenants.Acme, [TenantRoles.ContractsOfficer], "ar", Ct);
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var client = factory.ClientFor("acme.localhost");

        using var response = await AdminRequests.GetAsync(client, $"/admin/vendors/{companyId}", officer, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var raw = await response.Content.ReadAsStringAsync(Ct);
        AssertArabic(raw, "card");
        var html = WebUtility.HtmlDecode(raw);
        html.ShouldContain("شركة الاختبار");
        html.ShouldContain("شهادة السجل التجاري");
        html.ShouldContain("ساري");
        html.ShouldContain("منتهي");
        html.ShouldContain("استُبدل بملف أحدث");
        html.ShouldContain($"data-blocking=\"{VendorDocumentTypes.VatCertificate}:expired\"");
        // Gregorian years in both languages; ar-SA's default Umm al-Qura calendar would show another year for the same day.
        html.ShouldContain(validUntil.Year.ToString(System.Globalization.CultureInfo.InvariantCulture));
        html.ShouldNotContain(new System.Globalization.UmAlQuraCalendar().GetYear(validUntil.ToDateTime(TimeOnly.MinValue)).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("2026-09-27T20:59:59Z", "current")]
    [InlineData("2026-09-27T21:00:00Z", "expired")]
    public async Task The_vendor_card_marks_a_document_expired_from_riyadh_midnight_after_its_expiry_date(string utcNow, string status)
    {
        var (companyId, _) = await VendorAsync("Riyadh Midnight Page", TestTenants.Acme);
        await VendorDocumentRows.InsertAsync(db.OwnerConnectionString, companyId, VendorDocumentTypes.CrCertificate, new DateOnly(2026, 9, 27), "clean", isCurrent: true, Ct);
        var documentId = (await VendorDocumentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).Single().Id;
        var officer = await AdminRequests.MemberAsync(db, TestTenants.Acme, [TenantRoles.ContractsOfficer], "en", Ct);
        var clock = new FixedClock(DateTimeOffset.Parse(utcNow, CultureInfo.InvariantCulture));
        await using var factory = new PlatformWebFactory(db.AppConnectionString).WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock))));
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("http://acme.localhost"), AllowAutoRedirect = false });

        using var response = await AdminRequests.GetAsync(client, $"/admin/vendors/{companyId}", officer, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain($"data-document-status=\"{documentId}:{status}\"");
    }

    [Fact]
    public async Task A_vendor_without_a_verified_email_on_another_tenant_is_not_sent_to_the_join_page()
    {
        var (_, userId) = await VendorAsync("Unverified Redirect Company", TestTenants.Acme);
        var unverified = Vendor(userId, "en") with { EmailVerified = false };
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var beta = factory.ClientFor("beta.localhost");

        foreach (var path in new[] { "/vendor", "/" })
        {
            using var response = await beta.SendAsync(new HttpRequestMessage(HttpMethod.Get, path).As(unverified), Ct);
            response.Headers.Location?.OriginalString.ShouldNotBe("/vendor/join", path);
        }

        using var join = await beta.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/vendor/join").As(unverified), Ct);
        join.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Staff_and_anonymous_visitors_opening_the_home_are_not_sent_to_the_join_page()
    {
        var staff = await AdminRequests.MemberAsync(db, TestTenants.Beta, [TenantRoles.ContractsOfficer], "en", Ct);
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var beta = factory.ClientFor("beta.localhost");

        foreach (var user in new[] { staff, null })
        {
            foreach (var path in new[] { "/vendor", "/" })
            {
                using var response = await AdminRequests.GetAsync(beta, path, user, Ct);
                response.Headers.Location?.OriginalString.ShouldNotBe("/vendor/join", $"{user?.Subject ?? "anonymous"} {path}");
            }
        }
    }

    private static void AssertArabic(string html, string state)
    {
        html.ShouldContain("dir=\"rtl\"", Case.Sensitive, state);
        html.ShouldContain("lang=\"ar", Case.Sensitive, state);
        RawKey().Match(html).Value.ShouldBeEmpty(state);
    }

    private static Task<HttpResponseMessage> PostJoinAsync(HttpClient client, TestUser user, string page)
    {
        var form = JoinForm().Match(page);
        form.Success.ShouldBeTrue("the page renders the join form");
        var fields = HiddenInputs().Matches(form.Value)
            .ToDictionary(m => WebUtility.HtmlDecode(m.Groups["name"].Value), m => WebUtility.HtmlDecode(m.Groups["value"].Value), StringComparer.Ordinal);
        var request = new HttpRequestMessage(HttpMethod.Post, "/vendor/join") { Content = new FormUrlEncodedContent(fields) };
        return client.SendAsync(request.As(user), Ct);
    }

    private async Task<(Guid CompanyId, string UserId)> VendorAsync(string nameEn, TenantContext tenant)
    {
        var userId = Guid.NewGuid().ToString();
        var companyId = await VendorRows.RegisterAsync(db.AppConnectionString, tenant, userId, VendorRows.NewCrNumber(), nameEn, Ct);
        return (companyId, userId);
    }

    private static TestUser Vendor(string userId, string locale) =>
        new(userId, ["acme"], locale, RealmRoles: [IdentityClaims.VendorRealmRole], Email: $"{userId}@vendor.test", EmailVerified: true);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [GeneratedRegex("<form\\b[^>]*data-vendor-join-form[^>]*>.*?</form>", RegexOptions.Singleline)]
    private static partial Regex JoinForm();

    [GeneratedRegex("<input\\b(?=[^>]*type=\"hidden\")(?=[^>]*name=\"(?<name>[^\"]*)\")(?=[^>]*value=\"(?<value>[^\"]*)\")[^>]*>")]
    private static partial Regex HiddenInputs();

    // A shared resource key rendered as itself (Vendor.* or Admin.*), as a missing key would render.
    [GeneratedRegex(@"\b(Vendor|Admin)\.[A-Z][A-Za-z]+\.[A-Za-z.]+")]
    private static partial Regex RawKey();
}
