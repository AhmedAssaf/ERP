using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity;
using Platform.Modules.Identity.Members;
using Platform.Modules.Identity.Contracts;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// F-07 as narrowed (docs/05 row 4, spec 4.1) through the web host: role claims for the host tenant, the four tenant
/// policies, 403 with an audit row for a missing role, and denial audits throttled to one per user, host and path per
/// minute (which also closes W-27 for the cross-tenant denial). Each test uses its own user ids, so audit counts are exact.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class TenantRolesTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_member_gets_role_claims_for_the_host_tenant_only()
    {
        var user = new TestUser(Unique("both"), ["acme", "beta"]);
        await MemberRows.InsertAsync(db.AppConnectionString, TestTenants.Acme.TenantId, user.Subject, $"{user.Subject}@acme.test", [TenantRoles.TechnicalEvaluator], "active", Ct);
        await MemberRows.InsertAsync(db.AppConnectionString, TestTenants.Beta.TenantId, user.Subject, $"{user.Subject}@beta.test", [TenantRoles.TenantAdmin, TenantRoles.FinanceApprover], "active", Ct);
        var noRow = new TestUser(Unique("norow"), ["acme"]);
        await using var factory = Factory();

        (await GetTextAsync(factory, "acme.localhost", RoleEndpoints.RolesPath, user)).ShouldBe(TenantRoles.TechnicalEvaluator);
        (await GetTextAsync(factory, "beta.localhost", RoleEndpoints.RolesPath, user)).ShouldBe($"{TenantRoles.TenantAdmin},{TenantRoles.FinanceApprover}");
        // In the organization but without a member row: signed in, same tenant, no role.
        (await GetTextAsync(factory, "acme.localhost", RoleEndpoints.RolesPath, noRow)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(TenantPolicies.TenantAdmin, TenantRoles.TenantAdmin)]
    [InlineData(TenantPolicies.ContractsOfficer, TenantRoles.ContractsOfficer)]
    [InlineData(TenantPolicies.TechnicalEvaluator, TenantRoles.TechnicalEvaluator)]
    [InlineData(TenantPolicies.FinanceApprover, TenantRoles.FinanceApprover)]
    public async Task Each_tenant_policy_admits_its_role_only(string policy, string role)
    {
        var holder = new TestUser(Unique("holder"), ["acme"]);
        var other = new TestUser(Unique("other"), ["acme"]);
        await MemberRows.InsertAsync(db.AppConnectionString, TestTenants.Acme.TenantId, holder.Subject, $"{holder.Subject}@acme.test", [role], "active", Ct);
        await MemberRows.InsertAsync(
            db.AppConnectionString, TestTenants.Acme.TenantId, other.Subject, $"{other.Subject}@acme.test", [.. TenantRoles.All.Where(r => r != role)], "active", Ct);
        await using var factory = Factory();

        using var allowed = await GetAsync(factory, "acme.localhost", RoleEndpoints.PolicyPath(policy), holder);
        using var refused = await GetAsync(factory, "acme.localhost", RoleEndpoints.PolicyPath(policy), other);

        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_user_without_the_role_gets_403_and_one_audit_row()
    {
        // F-07's acceptance shape: a technical evaluator opening an admin-only endpoint.
        var evaluator = new TestUser(Unique("evaluator"), ["acme"]);
        await MemberRows.InsertAsync(db.AppConnectionString, TestTenants.Acme.TenantId, evaluator.Subject, $"{evaluator.Subject}@acme.test", [TenantRoles.TechnicalEvaluator], "active", Ct);
        await using var factory = Factory();

        using var response = await GetAsync(factory, "acme.localhost", RoleEndpoints.PolicyPath(TenantPolicies.TenantAdmin), evaluator);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await MemberRows.AuditCountAsync(db.OwnerConnectionString, TestTenants.Acme.TenantId, evaluator.Subject, "identity.role_denied", Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task A_role_claim_carried_by_the_token_grants_nothing()
    {
        var forger = new TestUser(Unique("forger"), ["acme"], TokenRoles: [TenantRoles.TenantAdmin]);
        await using var factory = Factory();

        using var response = await GetAsync(factory, "acme.localhost", RoleEndpoints.PolicyPath(TenantPolicies.TenantAdmin), forger);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Repeated_denials_within_a_minute_write_one_audit_row()
    {
        var evaluator = new TestUser(Unique("repeat"), ["acme"]);
        await MemberRows.InsertAsync(db.AppConnectionString, TestTenants.Acme.TenantId, evaluator.Subject, $"{evaluator.Subject}@acme.test", [TenantRoles.TechnicalEvaluator], "active", Ct);
        var clock = new ManualClock();
        await using var factory = Factory(clock);

        for (var i = 0; i < 3; i++)
        {
            using var denied = await GetAsync(factory, "acme.localhost", RoleEndpoints.PolicyPath(TenantPolicies.TenantAdmin), evaluator);
            denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            clock.Advance(TimeSpan.FromSeconds(20));
        }

        (await DeniedAsync(evaluator)).ShouldBe(1);

        // Another path is another attempt; a minute after the first row, the same path is audited again.
        using (await GetAsync(factory, "acme.localhost", RoleEndpoints.PolicyPath(TenantPolicies.FinanceApprover), evaluator))
        {
        }

        (await DeniedAsync(evaluator)).ShouldBe(2);
        using (await GetAsync(factory, "acme.localhost", RoleEndpoints.PolicyPath(TenantPolicies.TenantAdmin), evaluator))
        {
        }

        (await DeniedAsync(evaluator)).ShouldBe(3);
    }

    [Fact]
    public async Task Repeated_cross_tenant_denials_within_a_minute_write_one_audit_row()
    {
        // W-27: the same throttle covers the same-tenant check.
        var intruder = new TestUser(Unique("intruder"), ["beta"]);
        await using var factory = Factory(new ManualClock());

        for (var i = 0; i < 3; i++)
        {
            using var denied = await GetAsync(factory, "acme.localhost", "/", intruder);
            denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        (await MemberRows.AuditCountAsync(db.OwnerConnectionString, TestTenants.Acme.TenantId, intruder.Subject, "identity.cross_tenant_denied", Ct)).ShouldBe(1);
        // A user outside the tenant is audited once as cross-tenant, never also as a role denial.
        (await MemberRows.AuditCountAsync(db.OwnerConnectionString, TestTenants.Acme.TenantId, intruder.Subject, "identity.role_denied", Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task A_member_who_also_holds_the_vendor_realm_role_is_refused_by_every_staff_policy()
    {
        // The vendor role signs in without a second factor (V-4), so a principal holding it must never pass a staff
        // policy, even with a member row that grants every role.
        var both = new TestUser(Unique("staff-vendor"), ["acme"], RealmRoles: [IdentityClaims.VendorRealmRole]);
        await MemberRows.InsertAsync(db.AppConnectionString, TestTenants.Acme.TenantId, both.Subject, $"{both.Subject}@acme.test", [.. TenantRoles.All], "active", Ct);
        var staff = both with { Subject = Unique("staff-only"), RealmRoles = [] };
        await MemberRows.InsertAsync(db.AppConnectionString, TestTenants.Acme.TenantId, staff.Subject, $"{staff.Subject}@acme.test", [.. TenantRoles.All], "active", Ct);
        await using var factory = Factory();

        string[] paths =
        [
            RoleEndpoints.RolesPath, "/admin/staff", "/admin/branding",
            .. new[] { TenantPolicies.TenantAdmin, TenantPolicies.ContractsOfficer, TenantPolicies.TechnicalEvaluator, TenantPolicies.FinanceApprover }
                .Select(RoleEndpoints.PolicyPath),
        ];
        foreach (var path in paths)
        {
            using var refused = await GetAsync(factory, "acme.localhost", path, both);
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, path);
        }

        foreach (var path in paths.Where(p => p.StartsWith("/test/", StringComparison.Ordinal)))
        {
            using var allowed = await GetAsync(factory, "acme.localhost", path, staff);
            allowed.StatusCode.ShouldBe(HttpStatusCode.OK, path);
        }
    }

    [Fact]
    public async Task A_static_asset_request_through_the_host_does_not_read_the_member_table()
    {
        var invitee = new TestUser(Unique("asset"), ["acme"]);
        var email = $"{invitee.Subject}@acme.test";
        await MemberRows.InsertAsync(db.AppConnectionString, TestTenants.Acme.TenantId, invitee.Subject, email, [TenantRoles.TenantAdmin], "invited", Ct);
        await using var factory = Factory();

        using (await GetAsync(factory, "acme.localhost", "/_content/Platform.UI/css/app.css", invitee))
        {
        }

        // A member lookup activates an invited member, so an unchanged status proves the table was not read.
        (await MemberRows.FindByEmailAsync(db.AppConnectionString, TestTenants.Acme.TenantId, email, Ct))!.Status.ShouldBe("invited");
        (await GetTextAsync(factory, "acme.localhost", RoleEndpoints.RolesPath, invitee)).ShouldBe(TenantRoles.TenantAdmin);
        (await MemberRows.FindByEmailAsync(db.AppConnectionString, TestTenants.Acme.TenantId, email, Ct))!.Status.ShouldBe("active");
    }

    private Task<int> DeniedAsync(TestUser user) =>
        MemberRows.AuditCountAsync(db.OwnerConnectionString, TestTenants.Acme.TenantId, user.Subject, "identity.role_denied", Ct);

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private WebApplicationFactory<Program> Factory(TimeProvider? throttleClock = null) =>
        new PlatformWebFactory(db.AppConnectionString).WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IStartupFilter, RoleEndpoints>();
            if (throttleClock is not null)
            {
                services.Replace(ServiceDescriptor.Singleton(new DenialAuditThrottle(throttleClock)));
            }
        }));

    private static async Task<HttpResponseMessage> GetAsync(WebApplicationFactory<Program> factory, string host, string path, TestUser user)
    {
        using var client = factory.CreateClient(new() { BaseAddress = new Uri($"http://{host}"), AllowAutoRedirect = false });
        return await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, path).As(user), Ct);
    }

    private static async Task<string> GetTextAsync(WebApplicationFactory<Program> factory, string host, string path, TestUser user)
    {
        using var response = await GetAsync(factory, host, path, user);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync(Ct);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>
    /// Test-only endpoints: one echoing the caller's tenant roles (fallback policy), and one per tenant policy.
    /// </summary>
    private sealed class RoleEndpoints : IStartupFilter
    {
        public const string RolesPath = "/test/roles";

        public static string PolicyPath(string policy) => $"/test/policy/{policy}";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            var endpoints = app.Properties.TryGetValue("__EndpointRouteBuilder", out var value) && value is IEndpointRouteBuilder routeBuilder
                ? routeBuilder
                : throw new InvalidOperationException("The host did not expose its endpoint route builder.");
            endpoints.MapGet(RolesPath, (ClaimsPrincipal user) => Results.Text(string.Join(
                ',',
                user.Identities
                    .Where(i => i.AuthenticationType == MembersClaimsTransformation.AuthenticationType)
                    .SelectMany(i => i.FindAll(IdentityClaims.Role))
                    .Select(c => c.Value))));
            foreach (var policy in new[] { TenantPolicies.TenantAdmin, TenantPolicies.ContractsOfficer, TenantPolicies.TechnicalEvaluator, TenantPolicies.FinanceApprover })
            {
                endpoints.MapGet(PolicyPath(policy), () => Results.Ok()).RequireAuthorization(policy);
            }
        };
    }
}
