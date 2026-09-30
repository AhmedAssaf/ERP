using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Operations.Contracts;
using Platform.Modules.Vendors.Contracts;
using Platform.Shared.Results;
using static Platform.IntegrationTests.Security.CrAttack;

namespace Platform.IntegrationTests.Security;

/// <summary>
/// Pentest of the W-33 dispute path: <c>/vendor/dispute</c> for an anonymous visitor, a vendor user, a staff member and a
/// forged post; the one-open-per-company and three-open-per-person limits under concurrent submissions; and a claimant
/// resolving their own dispute.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed partial class CrDisputeAttackTests(DatabaseFixture db)
{
    // The endpoint ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_anonymous_visitor_neither_sees_the_form_nor_raises_a_dispute()
    {
        var (companyId, _, crNumber) = await VendorAsync(db, "Anonymous Dispute Co");
        await using var factory = Factory();
        using var client = Client(factory, "acme.localhost");

        using var page = await client.GetAsync(new Uri("/vendor/dispute", UriKind.Relative), Ct);
        page.StatusCode.ShouldNotBe(HttpStatusCode.OK);
        (await page.Content.ReadAsStringAsync(Ct)).ShouldNotContain("data-vendor-dispute");

        // A post with a token taken from a signed-in page, without the person.
        var applicant = Applicant();
        using var signedIn = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/vendor/dispute").As(applicant), Ct);
        using var posted = await PostAsync(client, null, await signedIn.Content.ReadAsStringAsync(Ct), crNumber);
        posted.StatusCode.ShouldNotBe(HttpStatusCode.OK);

        (await DisputeCountAsync(db, companyId)).ShouldBe(0);
    }

    [Fact]
    public async Task A_vendor_user_or_a_staff_member_is_refused_on_the_page()
    {
        var (companyId, _, crNumber) = await VendorAsync(db, "Refused Claimants Co");
        var (_, otherVendor, _) = await VendorAsync(db, "Claimant's Own Co");
        var staff = await StaffAsync(db, TestTenants.Acme, TenantRoles.ContractsOfficer);
        await using var factory = Factory();
        using var client = Client(factory, "acme.localhost");

        foreach (var user in new[]
                 {
                     new TestUser(otherVendor, ["acme"], "en", RealmRoles: ["vendor"], Email: $"{otherVendor}@vendor.test", EmailVerified: true),
                     new TestUser(staff, ["acme"], "en", Email: $"{staff}@acme.test", EmailVerified: true),
                 })
        {
            using var page = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/vendor/dispute").As(user), Ct);
            var html = await page.Content.ReadAsStringAsync(Ct);
            if (!html.Contains("data-vendor-dispute", StringComparison.Ordinal))
            {
                continue; // The page itself is closed to them: refused before the form.
            }

            using var response = await PostAsync(client, user, html, crNumber);
            (await response.Content.ReadAsStringAsync(Ct)).ShouldNotContain("data-dispute-raised");
        }

        (await DisputeCountAsync(db, companyId)).ShouldBe(0);
    }

    [Fact]
    public async Task A_post_without_the_antiforgery_token_raises_nothing()
    {
        var (companyId, _, crNumber) = await VendorAsync(db, "Forged Post Co");
        var applicant = Applicant();
        await using var factory = Factory();
        using var client = Client(factory, "acme.localhost");

        var fields = new Dictionary<string, string>
        {
            ["_handler"] = "vendor-dispute",
            ["Input.CrNumber"] = crNumber,
            ["Input.Statement"] = Statement,
            ["Input.AcceptedPrivacyNotice"] = VendorPrivacyNotice.CurrentVersion,
            ["Input.PrivacyNoticeCulture"] = VendorPrivacyNotice.English,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/vendor/dispute") { Content = new FormUrlEncodedContent(fields) };
        using var response = await client.SendAsync(request.As(applicant), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await DisputeCountAsync(db, companyId)).ShouldBe(0);
    }

    [Fact]
    public async Task The_dispute_page_does_not_exist_on_the_platform_host()
    {
        await using var factory = Factory();
        using var client = Client(factory, PlatformWebFactory.PlatformHost);

        using var page = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/vendor/dispute").As(Applicant()), Ct);

        page.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // The limits under concurrency ------------------------------------------------------------------------------------

    [Fact]
    public async Task Concurrent_submissions_for_one_company_open_one_dispute()
    {
        var (companyId, _, crNumber) = await VendorAsync(db, "Concurrent Same Co");
        var claimant = Guid.NewGuid().ToString();
        await using var host = Host();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            await using var scope = host.ScopeFor(TestTenants.Acme, actingUserId: claimant);
            return await scope.ServiceProvider.GetRequiredService<ICrDisputes>().RaiseAsync(Request(crNumber), "c@claimant.test", "Claimant", Ct);
        }, Ct)));

        results.Count(r => r.IsSuccess).ShouldBe(1);
        results.Where(r => !r.IsSuccess).ShouldAllBe(r => r.Error!.Code == CrDisputeErrors.AlreadyOpen);
        (await DisputeCountAsync(db, companyId)).ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_submissions_never_give_a_person_more_than_three_open_disputes()
    {
        // raise_cr_dispute counts the claimant's open disputes and then inserts, without a lock: two submissions whose
        // transactions overlap (the platform audit is written between the insert and the commit) both see two open ones.
        var companies = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            companies.Add((await VendorAsync(db, $"Limit Race Co {i}", certificate: false)).CrNumber);
        }

        var claimant = Guid.NewGuid().ToString();
        await using (var plain = Host())
        {
            foreach (var crNumber in companies.Take(2))
            {
                await using var scope = plain.ScopeFor(TestTenants.Acme, actingUserId: claimant);
                (await scope.ServiceProvider.GetRequiredService<ICrDisputes>().RaiseAsync(Request(crNumber), "c@claimant.test", "Claimant", Ct))
                    .IsSuccess.ShouldBeTrue();
            }
        }

        // Two submissions meet before either commits (the audit write is the rendezvous; it gives up after 5 seconds, so
        // a raise that waits in the database for the other one's commit does not hang the test).
        var rendezvous = new Rendezvous(2, TimeSpan.FromSeconds(5));
        await using var host = Host(services =>
        {
            var original = services.Last(d => d.ServiceType == typeof(IPlatformAudit));
            services.Replace(ServiceDescriptor.Scoped<IPlatformAudit>(sp =>
                new RendezvousAudit((IPlatformAudit)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!), rendezvous)));
        });

        var results = await Task.WhenAll(companies.Skip(2).Select(crNumber => Task.Run(async () =>
        {
            await using var scope = host.ScopeFor(TestTenants.Acme, actingUserId: claimant);
            return await scope.ServiceProvider.GetRequiredService<ICrDisputes>().RaiseAsync(Request(crNumber), "c@claimant.test", "Claimant", Ct);
        }, Ct)));

        var open = await OpenDisputesOfAsync(db, claimant);
        open.ShouldBeLessThanOrEqualTo(3, $"the limit of three open disputes was passed by concurrent submissions ({string.Join(", ", results.Select(r => r.IsSuccess ? "raised" : r.Error.Code))})");
    }

    // Resolution ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_claimant_cannot_uphold_their_own_dispute()
    {
        var (firstCompany, firstOwner, firstCr) = await VendorAsync(db, "Self Uphold Co");
        var (secondCompany, secondOwner, secondCr) = await VendorAsync(db, "Self Uphold Db Co");
        var claimant = Guid.NewGuid().ToString();
        await using var host = Host();
        var first = await RaiseAsync(host, claimant, firstCr);
        var second = await RaiseAsync(host, claimant, secondCr);

        // The console's service, in a platform scope whose acting user is the claimant.
        Result<CrDisputeUpheld>? upheld = null;
        try
        {
            await using var scope = host.PlatformScope(claimant);
            upheld = await scope.ServiceProvider.GetRequiredService<ICrOwnershipAdministration>().UpholdAsync(first, "I checked my own claim.", claimant, Ct);
        }
        catch (InvalidOperationException)
        {
            // Refused: the property holds.
        }

        // The database function, in a session without tenant or vendor context whose acting user is the claimant.
        var refusedInDatabase = false;
        await using (var session = await OwnershipRows.AppSessionAsync(db.AppConnectionString, null, null, claimant, Ct))
        await using (var command = new NpgsqlCommand("select * from vendor.resolve_cr_dispute(@id, true, 'My own claim.')", session))
        {
            command.Parameters.AddWithValue("id", second);
            try
            {
                await command.ExecuteNonQueryAsync(Ct);
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.CheckViolation)
            {
                refusedInDatabase = true;
            }
        }

        var owners = new[]
        {
            (await OwnershipRows.VendorUsersAsync(db.OwnerConnectionString, firstCompany, Ct)).Select(u => u.UserId).Single(),
            (await OwnershipRows.VendorUsersAsync(db.OwnerConnectionString, secondCompany, Ct)).Select(u => u.UserId).Single(),
        };
        (upheld?.IsSuccess ?? false).ShouldBeFalse("the console service let the claimant uphold their own dispute");
        refusedInDatabase.ShouldBeTrue("resolve_cr_dispute let the claimant uphold their own dispute");
        owners.ShouldBe([firstOwner, secondOwner]);
    }

    private WebApplicationFactory<Program> Factory() =>
        new PlatformWebFactory(db.AppConnectionString).WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => new FakeVendorAccounts()))));

    private static HttpClient Client(WebApplicationFactory<Program> factory, string host) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://{host}"), AllowAutoRedirect = false });

    private ModuleHost Host(Action<IServiceCollection>? configure = null) =>
        new(db.AppConnectionString, configure: services =>
        {
            services.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => new FakeVendorAccounts()));
            configure?.Invoke(services);
        });

    private static async Task<Guid> RaiseAsync(ModuleHost host, string claimant, string crNumber)
    {
        await using var scope = host.ScopeFor(TestTenants.Acme, actingUserId: claimant);
        var raised = await scope.ServiceProvider.GetRequiredService<ICrDisputes>().RaiseAsync(Request(crNumber), $"{claimant}@claimant.test", "Claimant", Ct);
        raised.IsSuccess.ShouldBeTrue(raised.Error?.Message);
        return raised.Value;
    }

    private static TestUser Applicant()
    {
        var subject = Guid.NewGuid().ToString();
        return new TestUser(subject, [], "en", Email: $"{subject}@claimant.test", EmailVerified: true);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, TestUser? user, string page, string crNumber)
    {
        var form = DisputeForm().Match(page);
        form.Success.ShouldBeTrue("the page renders the dispute form");
        var fields = HiddenInputs().Matches(form.Value)
            .ToDictionary(m => WebUtility.HtmlDecode(m.Groups["name"].Value), m => WebUtility.HtmlDecode(m.Groups["value"].Value), StringComparer.Ordinal);
        fields["Input.CrNumber"] = crNumber;
        fields["Input.Statement"] = Statement;
        fields["Input.AcceptedPrivacyNotice"] = VendorPrivacyNotice.CurrentVersion;
        var request = new HttpRequestMessage(HttpMethod.Post, "/vendor/dispute") { Content = new FormUrlEncodedContent(fields) };
        return client.SendAsync(user is null ? request : request.As(user), Ct);
    }

    [GeneratedRegex("<form\\b[^>]*data-vendor-dispute[^>]*>.*?</form>", RegexOptions.Singleline)]
    private static partial Regex DisputeForm();

    [GeneratedRegex("<input\\b(?=[^>]*type=\"hidden\")(?=[^>]*name=\"(?<name>[^\"]*)\")(?=[^>]*value=\"(?<value>[^\"]*)\")[^>]*>")]
    private static partial Regex HiddenInputs();

    /// <summary>Lets <c>count</c> callers through together, or each one alone after <c>timeout</c>.</summary>
    private sealed class Rendezvous(int count, TimeSpan timeout)
    {
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public async Task ArriveAsync()
        {
            if (Interlocked.Increment(ref _arrived) >= count)
            {
                _all.TrySetResult();
            }

            await Task.WhenAny(_all.Task, Task.Delay(timeout));
        }
    }

    /// <summary>The real platform audit, entered only after every dispute submission of the race reached it.</summary>
    private sealed class RendezvousAudit(IPlatformAudit inner, Rendezvous rendezvous) : IPlatformAudit
    {
        public async Task WriteAsync(PlatformAuditEntry entry, CancellationToken cancellationToken = default)
        {
            if (entry.Action == "vendor.dispute_raised")
            {
                await rendezvous.ArriveAsync();
            }

            await inner.WriteAsync(entry, cancellationToken);
        }
    }
}
