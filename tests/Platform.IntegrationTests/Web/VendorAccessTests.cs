using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Platform.IntegrationTests.Infrastructure;
using Platform.IntegrationTests.Vendors;
using Platform.Modules.Identity.Contracts;
using Platform.Shared.Tenancy;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// The Vendor policy and the vendor context on tenant hosts (vendor plan task 2, spec section 3, V-3): a vendor is a
/// signed-in user with a verified email, the realm role <c>vendor</c>, a <c>vendor.vendor_users</c> row and membership of
/// the host tenant's organization; the vendor context (and with it the vendor row-level security) is set only after
/// that policy passed, on requests and on circuits. Staff and vendors never cross: staff policies stay closed to vendors,
/// the vendor policy to staff. The registration form refuses input in the user's own language.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed partial class VendorAccessTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_vendor_sees_its_own_company_on_the_vendor_home()
    {
        var (vendor, _) = await VendorAsync("Home Company Trading");
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var client = factory.ClientFor("acme.localhost");

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/vendor").As(vendor), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(Ct);
        html.ShouldContain("data-vendor-company");
        html.ShouldContain("Home Company Trading");
    }

    [Fact]
    public async Task A_vendor_cannot_open_staff_pages()
    {
        var (vendor, _) = await VendorAsync("Staff Pages Probe");
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var client = factory.ClientFor("acme.localhost");

        foreach (var path in new[] { "/admin/staff", "/admin/branding" })
        {
            using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, path).As(vendor), Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, path);
        }
    }

    [Fact]
    public async Task A_staff_account_cannot_act_as_a_vendor()
    {
        var subject = $"staff-{Guid.NewGuid():N}";
        await MemberRows.InsertAsync(db.AppConnectionString, TestTenants.Acme.TenantId, subject, $"{subject}@acme.test", [TenantRoles.TenantAdmin], "active", Ct);
        var staff = new TestUser(subject, ["acme"], "en", Email: $"{subject}@acme.test", EmailVerified: true);
        // A token that somehow carried the vendor role still finds no vendor.vendor_users row.
        var forged = staff with { RealmRoles = [IdentityClaims.VendorRealmRole] };
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var client = factory.ClientFor("acme.localhost");

        foreach (var user in new[] { staff, forged })
        {
            using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/vendor").As(user), Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using var form = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/vendor/register/company").As(staff), Ct);
        var html = await form.Content.ReadAsStringAsync(Ct);
        using var submitted = await PostRegistrationAsync(client, staff, html, VendorRegistrationInputTests.Valid());
        (await submitted.Content.ReadAsStringAsync(Ct)).ShouldContain(
            "This account belongs to a staff member. Register your company with a separate account.");
    }

    [Fact]
    public async Task A_vendor_of_another_tenant_is_challenged_until_it_joins()
    {
        var (vendor, companyId) = await VendorAsync("Cross Tenant Supplies");
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var client = factory.ClientFor("beta.localhost");

        using (var before = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/vendor").As(vendor), Ct))
        {
            before.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        // Joining (task 5) creates the relationship and the organization membership the next token carries.
        await VendorRows.RelateAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, companyId, Ct);
        var joined = vendor with { Organizations = ["acme", "beta"] };

        using var after = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/vendor").As(joined), Ct);
        after.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await after.Content.ReadAsStringAsync(Ct)).ShouldContain("Cross Tenant Supplies");
    }

    [Fact]
    public async Task Without_a_verified_email_the_vendor_role_or_a_vendor_row_the_vendor_home_is_refused()
    {
        var (vendor, _) = await VendorAsync("Partial Vendor");
        var noRow = vendor with { Subject = Guid.NewGuid().ToString() };
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var client = factory.ClientFor("acme.localhost");

        foreach (var user in new[] { vendor with { EmailVerified = false }, vendor with { RealmRoles = [] }, noRow })
        {
            using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/vendor").As(user), Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
    }

    [Theory]
    [InlineData("en", "Enter the 10-digit commercial registration (CR) number, using digits only.")]
    [InlineData("ar", "أدخل رقم السجل التجاري المكوّن من 10 أرقام، بالأرقام فقط.")]
    public async Task A_cr_number_that_is_not_ten_digits_shows_a_specific_error_in_the_vendors_language(string locale, string message)
    {
        var applicant = Applicant(locale);
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var client = factory.ClientFor("acme.localhost");
        using var page = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/vendor/register/company").As(applicant), Ct);
        page.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var response = await PostRegistrationAsync(
            client, applicant, await page.Content.ReadAsStringAsync(Ct), VendorRegistrationInputTests.Valid() with { CrNumber = "12345" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Ct));
        html.ShouldContain(message);
        (await VendorRows.FindUserAsync(db.OwnerConnectionString, applicant.Subject, Ct)).ShouldBeNull();
    }

    [Theory]
    [InlineData("en", "Privacy notice")]
    [InlineData("ar", "إشعار الخصوصية")]
    public async Task The_registration_form_shows_the_versioned_privacy_notice_in_the_users_language(string locale, string heading)
    {
        var applicant = Applicant(locale);
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var client = factory.ClientFor("acme.localhost");

        using var page = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/vendor/register/company").As(applicant), Ct);

        var html = WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync(Ct));
        html.ShouldContain(heading);
        html.ShouldContain("data-privacy-notice=\"V1\"");
        html.ShouldContain("name=\"Input.AcceptedPrivacyNotice\"");
        html.ShouldNotContain("Vendor.");
    }

    [Fact]
    public async Task An_account_without_a_verified_email_cannot_open_the_registration_form()
    {
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var client = factory.ClientFor("acme.localhost");

        using var page = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/vendor/register/company").As(Applicant("en") with { EmailVerified = false }), Ct);

        page.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_vendor_circuit_gets_the_company_of_the_connection_user_after_the_policy_passes()
    {
        var (vendor, companyId) = await VendorAsync("Circuit Company");
        await using var factory = new PlatformWebFactory(db.AppConnectionString);

        (await OpenCircuitAsync(factory, Principal(vendor))).ShouldBe(companyId);
        (await OpenCircuitAsync(factory, Principal(vendor with { Organizations = ["beta"] }))).ShouldBeNull();
        (await OpenCircuitAsync(factory, Principal(vendor with { RealmRoles = [] }))).ShouldBeNull();
        (await OpenCircuitAsync(factory, new ClaimsPrincipal(new ClaimsIdentity()))).ShouldBeNull();
    }

    [Fact]
    public async Task A_circuit_acts_as_the_subject_of_its_connection_user_on_tenant_and_platform_hosts()
    {
        var (vendor, _) = await VendorAsync("Acting User Company");
        var applicant = Applicant("en");
        await using var factory = new PlatformWebFactory(db.AppConnectionString);

        (await OpenCircuitAsync(factory, Principal(vendor), "acme.localhost")).ActingUser.ShouldBe(vendor.Subject);
        // An applicant has no vendor role yet and no vendor context, but registering needs the acting user.
        var (company, actingUser) = await OpenCircuitAsync(factory, Principal(applicant), "acme.localhost");
        company.ShouldBeNull();
        actingUser.ShouldBe(applicant.Subject);
        (await OpenCircuitAsync(factory, Principal(applicant), "platform.localhost")).ActingUser.ShouldBe(applicant.Subject);
        (await OpenCircuitAsync(factory, new ClaimsPrincipal(new ClaimsIdentity()), "acme.localhost")).ActingUser.ShouldBeNull();
    }

    /// <summary>
    /// Runs the host's vendor circuit handler for an acme circuit (the tenant handler, which runs first, has set the
    /// tenant) whose connection request carried <paramref name="user"/>.
    /// </summary>
    private static async Task<Guid?> OpenCircuitAsync(PlatformWebFactory factory, ClaimsPrincipal user) =>
        (await OpenCircuitAsync(factory, user, "acme.localhost")).Company;

    /// <summary>
    /// Runs the host's vendor circuit handler for a circuit on <paramref name="host"/> (the tenant handler, which runs
    /// first, has set the tenant or the platform mark) whose connection request carried <paramref name="user"/>.
    /// </summary>
    private static async Task<(Guid? Company, string? ActingUser)> OpenCircuitAsync(PlatformWebFactory factory, ClaimsPrincipal user, string host)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = new DefaultHttpContext { User = user, RequestServices = scope.ServiceProvider };
        context.Request.Host = new HostString(host);
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
        if (host.StartsWith("platform.", StringComparison.Ordinal))
        {
            Platform.Web.PlatformHost.PlatformRequest.Mark(context);
            scope.ServiceProvider.GetRequiredService<PlatformRequestContext>().MarkPlatform();
        }
        else
        {
            scope.ServiceProvider.GetRequiredService<TenantAccessor>().Set(TestTenants.Acme);
        }
        var handlers = scope.ServiceProvider.GetServices<CircuitHandler>().ToList();
        var vendorHandler = handlers.OfType<Platform.Web.Vendor.VendorCircuitHandler>().ShouldHaveSingleItem();
        vendorHandler.Order.ShouldBeGreaterThan(handlers.OfType<Platform.Web.Tenancy.TenantCircuitHandler>().Single().Order);

        await vendorHandler.OnCircuitOpenedAsync(null!, Ct);

        return (scope.ServiceProvider.GetRequiredService<IVendorAccessor>().Current?.CompanyId,
            scope.ServiceProvider.GetRequiredService<IActingUserAccessor>().UserId);
    }

    private static ClaimsPrincipal Principal(TestUser user)
    {
        var claims = new List<Claim> { new("sub", user.Subject) };
        claims.AddRange(user.Organizations.Select(o => new Claim("organization", o)));
        claims.AddRange((user.RealmRoles ?? []).Select(r => new Claim("roles", r)));
        if (user.EmailVerified)
        {
            claims.Add(new Claim("email_verified", "true"));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test", "sub", "role"));
    }

    private async Task<(TestUser User, Guid CompanyId)> VendorAsync(string nameEn)
    {
        var subject = Guid.NewGuid().ToString();
        var companyId = await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Acme, subject, VendorRows.NewCrNumber(), nameEn, Ct);
        return (new TestUser(subject, ["acme"], "en", RealmRoles: [IdentityClaims.VendorRealmRole], Email: $"{subject}@vendor.test", EmailVerified: true), companyId);
    }

    private static TestUser Applicant(string locale)
    {
        var subject = Guid.NewGuid().ToString();
        return new TestUser(subject, [], locale, Email: $"{subject}@applicant.test", EmailVerified: true);
    }

    /// <summary>Posts the registration form rendered in <paramref name="page"/> (antiforgery token and handler included).</summary>
    internal static Task<HttpResponseMessage> PostRegistrationAsync(
        HttpClient client, TestUser? user, string page, Platform.Modules.Vendors.Contracts.VendorRegistration input)
    {
        var form = RegistrationForm().Match(page);
        form.Success.ShouldBeTrue("the page renders the registration form");
        var fields = HiddenInputs().Matches(form.Value)
            .ToDictionary(m => WebUtility.HtmlDecode(m.Groups["name"].Value), m => WebUtility.HtmlDecode(m.Groups["value"].Value), StringComparer.Ordinal);
        fields["Input.CrNumber"] = input.CrNumber ?? string.Empty;
        fields["Input.NameAr"] = input.NameAr ?? string.Empty;
        fields["Input.NameEn"] = input.NameEn ?? string.Empty;
        fields["Input.VatNumber"] = input.VatNumber ?? string.Empty;
        fields["Input.Address"] = input.Address ?? string.Empty;
        fields["Input.ContactName"] = input.ContactName ?? string.Empty;
        fields["Input.ContactPhone"] = input.ContactPhone ?? string.Empty;
        fields["Input.ContactEmail"] = input.ContactEmail ?? string.Empty;
        if (input.AcceptedPrivacyNotice is { } accepted)
        {
            fields["Input.AcceptedPrivacyNotice"] = accepted;
        }

        var request = new HttpRequestMessage(HttpMethod.Post, "/vendor/register/company") { Content = new FormUrlEncodedContent(fields) };
        return client.SendAsync(user is null ? request : request.As(user), Ct);
    }

    [GeneratedRegex("<form\\b[^>]*data-vendor-register[^>]*>.*?</form>", RegexOptions.Singleline)]
    private static partial Regex RegistrationForm();

    [GeneratedRegex("<input\\b(?=[^>]*type=\"hidden\")(?=[^>]*name=\"(?<name>[^\"]*)\")(?=[^>]*value=\"(?<value>[^\"]*)\")[^>]*>")]
    private static partial Regex HiddenInputs();
}
