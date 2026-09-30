using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.IntegrationTests.Infrastructure;
using Platform.IntegrationTests.Vendors;
using Platform.Modules.Identity.Contracts;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// W-33, the real company's way back through the web host: a registration refused because the CR number is taken links
/// to <c>/vendor/dispute</c>, where a signed-in applicant sends a request that WaslaBid reviews, sees it listed, and gets
/// every field error in their language. The claimant and their email come from the principal, never the form.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed partial class VendorDisputePageTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_registration_refused_for_a_taken_cr_number_links_to_the_dispute_page()
    {
        var crNumber = VendorRows.NewCrNumber();
        await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Acme, Guid.NewGuid().ToString(), crNumber, "Taken Number Co", Ct);
        var applicant = Applicant("en");
        await using var factory = Factory();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("http://acme.localhost"), AllowAutoRedirect = false });
        using var page = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/vendor/register/company").As(applicant), Ct);

        using var response = await VendorAccessTests.PostRegistrationAsync(
            client, applicant, await page.Content.ReadAsStringAsync(Ct), VendorRegistrationInputTests.Valid() with { CrNumber = crNumber });

        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Ct));
        html.ShouldContain("This company already has an account on WaslaBid.");
        html.ShouldContain("href=\"vendor/dispute\"");
        html.ShouldContain("Is this your company? Ask WaslaBid to review who owns it.");
    }

    [Fact]
    public async Task An_applicant_sends_a_dispute_and_sees_it_listed_under_review()
    {
        var crNumber = VendorRows.NewCrNumber();
        var companyId = await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Acme, Guid.NewGuid().ToString(), crNumber, "Disputed On Page Co", Ct);
        var applicant = Applicant("en");
        await using var factory = Factory();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("http://acme.localhost"), AllowAutoRedirect = false });

        using var page = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/vendor/dispute").As(applicant), Ct);
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        var form = await page.Content.ReadAsStringAsync(Ct);
        form.ShouldContain("data-vendor-dispute");

        using var response = await PostAsync(client, applicant, form, crNumber, "I am the owner named on the certificate.", accepted: true);

        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Ct));
        html.ShouldContain("data-dispute-raised");
        html.ShouldContain($"WaslaBid will contact you at {applicant.Email}.");
        html.ShouldContain($"data-own-dispute=\"{crNumber}\"");
        html.ShouldContain("data-own-dispute-status=\"open\"");
        // Nothing moves until a platform admin upholds the request.
        (await OwnershipRows.VendorUsersAsync(db.OwnerConnectionString, companyId, Ct)).ShouldHaveSingleItem().UserId.ShouldNotBe(applicant.Subject);
        (await VendorRows.PlatformAuditsAsync(db.OwnerConnectionString, applicant.Subject, "vendor.dispute_raised", Ct)).ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData("en", "Enter the 10-digit commercial registration (CR) number", "Explain in up to 2,000 characters why the company is yours.")]
    [InlineData("ar", "dir=\"rtl\"", "اشرح في حدود 2,000 حرف لماذا المنشأة لك.")]
    public async Task Every_field_error_shows_at_once_in_the_persons_language(string locale, string first, string second)
    {
        var applicant = Applicant(locale);
        await using var factory = Factory();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("http://acme.localhost"), AllowAutoRedirect = false });
        using var page = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/vendor/dispute").As(applicant), Ct);

        using var response = await PostAsync(client, applicant, await page.Content.ReadAsStringAsync(Ct), "12", " ", accepted: false);

        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Ct));
        html.ShouldContain(first);
        html.ShouldContain(second);
        html.ShouldNotContain("data-dispute-raised");
        html.ShouldNotContain("Vendor.Dispute.", Case.Sensitive, "no raw resource key");
    }

    [Fact]
    public async Task A_multi_line_statement_posted_from_the_form_is_recorded_with_its_line_breaks()
    {
        var crNumber = VendorRows.NewCrNumber();
        var companyId = await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Acme, Guid.NewGuid().ToString(), crNumber, "Multi Line Page Co", Ct);
        var applicant = Applicant("en");
        await using var factory = Factory();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("http://acme.localhost"), AllowAutoRedirect = false });
        using var page = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/vendor/dispute").As(applicant), Ct);

        using var response = await PostAsync(client, applicant, await page.Content.ReadAsStringAsync(Ct), crNumber,
            "I own the company.\r\nMy name is on the certificate.", accepted: true);

        WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Ct)).ShouldContain("data-dispute-raised");
        await using var owner = new Npgsql.NpgsqlConnection(db.OwnerConnectionString);
        await owner.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand("select statement from vendor.cr_disputes where company_id = @company", owner);
        command.Parameters.AddWithValue("company", companyId);
        ((string)(await command.ExecuteScalarAsync(Ct))!).ShouldBe("I own the company.\nMy name is on the certificate.");
    }

    [Fact]
    public async Task Every_post_counts_toward_the_limit_and_the_sixth_in_fifteen_minutes_is_refused()
    {
        var crNumber = VendorRows.NewCrNumber();
        await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Acme, Guid.NewGuid().ToString(), crNumber, "Rate Limited Co", Ct);
        var applicant = Applicant("en");
        var other = Applicant("en");
        await using var factory = Factory();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("http://acme.localhost"), AllowAutoRedirect = false });
        using var page = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/vendor/dispute").As(applicant), Ct);
        var form = await page.Content.ReadAsStringAsync(Ct);

        // Valid, invalid and repeated posts all count; reading the page does not.
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            using var response = await PostAsync(client, applicant, form, i % 2 == 0 ? crNumber : "12", "I am the owner.", accepted: true);
            statuses.Add(response.StatusCode);
        }

        statuses.Take(5).ShouldAllBe(s => s == HttpStatusCode.OK);
        statuses[5].ShouldBe(HttpStatusCode.TooManyRequests);
        using (var again = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/vendor/dispute").As(applicant), Ct))
        {
            again.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Another person is not limited by the first one's posts.
        using var otherPage = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/vendor/dispute").As(other), Ct);
        using var otherPost = await PostAsync(client, other, await otherPage.Content.ReadAsStringAsync(Ct), crNumber, "I am the owner.", accepted: true);
        otherPost.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private WebApplicationFactory<Program> Factory() =>
        new PlatformWebFactory(db.AppConnectionString).WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => new FakeVendorAccounts()))));

    private static TestUser Applicant(string locale)
    {
        var subject = Guid.NewGuid().ToString();
        return new TestUser(subject, [], locale, Email: $"{subject}@claimant.test", EmailVerified: true);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, TestUser user, string page, string crNumber, string statement, bool accepted)
    {
        var form = DisputeForm().Match(page);
        form.Success.ShouldBeTrue("the page renders the dispute form");
        var fields = HiddenInputs().Matches(form.Value)
            .ToDictionary(m => WebUtility.HtmlDecode(m.Groups["name"].Value), m => WebUtility.HtmlDecode(m.Groups["value"].Value), StringComparer.Ordinal);
        fields["Input.CrNumber"] = crNumber;
        fields["Input.Statement"] = statement;
        if (accepted)
        {
            fields["Input.AcceptedPrivacyNotice"] = "V1";
        }

        var request = new HttpRequestMessage(HttpMethod.Post, "/vendor/dispute") { Content = new FormUrlEncodedContent(fields) };
        return client.SendAsync(request.As(user), Ct);
    }

    [GeneratedRegex("<form\\b[^>]*data-vendor-dispute[^>]*>.*?</form>", RegexOptions.Singleline)]
    private static partial Regex DisputeForm();

    [GeneratedRegex("<input\\b(?=[^>]*type=\"hidden\")(?=[^>]*name=\"(?<name>[^\"]*)\")(?=[^>]*value=\"(?<value>[^\"]*)\")[^>]*>")]
    private static partial Regex HiddenInputs();
}
