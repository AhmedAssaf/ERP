using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity;
using Platform.Modules.Identity.Members;
using Platform.Modules.Identity.Contracts;
using Platform.Shared.Tenancy;

namespace Platform.IntegrationTests.Identity;

/// <summary>
/// The members claims transformation (spec 4.1): role claims for the host tenant from identity.members, first sign-in
/// activation, and the lazy binding of a member row seeded by email to the Keycloak <c>sub</c> (verified email only).
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class MembersClaimsTransformationTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_seeded_member_is_bound_by_verified_email_on_first_sign_in()
    {
        var tenant = MemberRows.NewTenant();
        const string email = "admin@seeded.test";
        await MemberRows.InsertAsync(db.AppConnectionString, tenant.TenantId, null, email, [TenantRoles.TenantAdmin], "invited", Ct);
        await using var host = new ModuleHost(db.AppConnectionString);

        // An unverified address proves nothing about who holds it: no roles and no binding.
        (await RolesAsync(host, tenant, User("sub-1", tenant, "Admin@Seeded.test", verified: false))).ShouldBeEmpty();
        (await MemberRows.FindByEmailAsync(db.AppConnectionString, tenant.TenantId, email, Ct))!.UserId.ShouldBeNull();

        (await RolesAsync(host, tenant, User("sub-1", tenant, "Admin@Seeded.test", verified: true))).ShouldBe([TenantRoles.TenantAdmin]);
        var bound = await MemberRows.FindByEmailAsync(db.AppConnectionString, tenant.TenantId, email, Ct);
        bound!.UserId.ShouldBe("sub-1");
        bound.Status.ShouldBe("active");
        bound.ActivatedAt.ShouldNotBeNull();
        (await MemberRows.AuditCountAsync(db.OwnerConnectionString, tenant.TenantId, "sub-1", "identity.member_activated", Ct)).ShouldBe(1);

        // Once bound, the row belongs to that sub; another account with the same verified address gets nothing.
        (await RolesAsync(host, tenant, User("sub-2", tenant, email, verified: true))).ShouldBeEmpty();
        (await RolesAsync(host, tenant, User("sub-1", tenant, email, verified: true))).ShouldBe([TenantRoles.TenantAdmin]);
        (await MemberRows.AuditCountAsync(db.OwnerConnectionString, tenant.TenantId, "sub-1", "identity.member_activated", Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task An_invited_member_becomes_active_on_first_sign_in()
    {
        var tenant = MemberRows.NewTenant();
        await MemberRows.InsertAsync(db.AppConnectionString, tenant.TenantId, "invitee", "invitee@t.test", [TenantRoles.TechnicalEvaluator], "invited", Ct);
        await using var host = new ModuleHost(db.AppConnectionString);

        (await RolesAsync(host, tenant, User("invitee", tenant, email: null, verified: false))).ShouldBe([TenantRoles.TechnicalEvaluator]);

        var row = await MemberRows.FindByEmailAsync(db.AppConnectionString, tenant.TenantId, "invitee@t.test", Ct);
        row!.Status.ShouldBe("active");
        row.ActivatedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_user_outside_the_tenants_organization_gets_no_roles_even_with_a_member_row()
    {
        var tenant = MemberRows.NewTenant();
        await MemberRows.InsertAsync(db.AppConnectionString, tenant.TenantId, "removed", "removed@t.test", [TenantRoles.TenantAdmin], "active", Ct);
        await using var host = new ModuleHost(db.AppConnectionString);
        var user = AuthCookies.Principal([new Claim("sub", "removed"), new Claim("organization", "some-other-org")]);

        (await RolesAsync(host, tenant, user)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_role_claim_the_principal_already_carries_is_replaced_not_trusted()
    {
        var tenant = MemberRows.NewTenant();
        await using var host = new ModuleHost(db.AppConnectionString);
        var forged = User("no-row", tenant, email: null, verified: false);
        forged.AddIdentity(new ClaimsIdentity([new Claim(IdentityClaims.Role, TenantRoles.TenantAdmin)], MembersClaimsTransformation.AuthenticationType));

        (await RolesAsync(host, tenant, forged)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("/_framework/blazor.web.js")]
    [InlineData("/_content/Platform.UI/css/app.css")]
    [InlineData("/css/site.css")]
    [InlineData("/fonts/plex.woff2")]
    [InlineData("/favicon.ico")]
    [InlineData("/health")]
    public async Task A_static_or_shared_request_does_not_look_up_the_member(string path)
    {
        var tenant = MemberRows.NewTenant();
        await MemberRows.InsertAsync(db.AppConnectionString, tenant.TenantId, "static", "static@t.test", [TenantRoles.TenantAdmin], "invited", Ct);
        await using var host = new ModuleHost(db.AppConnectionString);
        var accessor = host.Services.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext { Request = { Path = path } };
        try
        {
            var user = User("static", tenant, email: null, verified: false);
            await using var scope = host.ScopeFor(tenant);
            var transformed = await scope.ServiceProvider.GetRequiredService<IClaimsTransformation>().TransformAsync(user);

            transformed.ShouldBeSameAs(user);
        }
        finally
        {
            accessor.HttpContext = null;
        }

        // Had identity.members been read, the invited member would now be active.
        (await MemberRows.FindByEmailAsync(db.AppConnectionString, tenant.TenantId, "static@t.test", Ct))!.Status.ShouldBe("invited");
    }

    [Fact]
    public async Task Roles_are_read_once_per_thirty_seconds_per_tenant_and_user()
    {
        var tenant = MemberRows.NewTenant();
        await MemberRows.InsertAsync(db.AppConnectionString, tenant.TenantId, "cached", "cached@t.test", [TenantRoles.TechnicalEvaluator], "active", Ct);
        var clock = new ManualClock();
        await using var host = new ModuleHost(db.AppConnectionString, clock: clock);
        var user = User("cached", tenant, email: null, verified: false);

        (await RolesAsync(host, tenant, user)).ShouldBe([TenantRoles.TechnicalEvaluator]);
        // Changed behind the directory's back: a second request within 30 seconds does not read the table again.
        await MemberRows.OverwriteRolesAsync(db.AppConnectionString, tenant.TenantId, "cached", [TenantRoles.FinanceApprover], Ct);
        clock.Advance(TimeSpan.FromSeconds(29));
        (await RolesAsync(host, tenant, user)).ShouldBe([TenantRoles.TechnicalEvaluator]);

        clock.Advance(TimeSpan.FromSeconds(1));
        (await RolesAsync(host, tenant, user)).ShouldBe([TenantRoles.FinanceApprover]);
        MemberRolesCache.CacheFor.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task A_role_change_through_the_directory_takes_effect_on_the_next_request()
    {
        var tenant = MemberRows.NewTenant();
        await MemberRows.InsertAsync(db.AppConnectionString, tenant.TenantId, "admin-a", "a@t.test", [TenantRoles.TenantAdmin], "active", Ct);
        await MemberRows.InsertAsync(db.AppConnectionString, tenant.TenantId, "changed", "changed@t.test", [TenantRoles.TenantAdmin], "active", Ct);
        await using var host = new ModuleHost(db.AppConnectionString, clock: new ManualClock());
        var user = User("changed", tenant, email: null, verified: false);
        (await RolesAsync(host, tenant, user)).ShouldBe([TenantRoles.TenantAdmin]);

        await using (var scope = host.ScopeFor(tenant))
        {
            var changed = await scope.ServiceProvider.GetRequiredService<IMemberDirectory>()
                .SetRolesAsync("changed", [TenantRoles.ContractsOfficer], "admin-a", Ct);
            changed.IsSuccess.ShouldBeTrue();
        }

        (await RolesAsync(host, tenant, user)).ShouldBe([TenantRoles.ContractsOfficer]);
    }

    [Fact]
    public async Task Cached_roles_belong_to_one_tenant()
    {
        var acme = MemberRows.NewTenant();
        var beta = MemberRows.NewTenant();
        await MemberRows.InsertAsync(db.AppConnectionString, acme.TenantId, "both", "both@acme.test", [TenantRoles.TenantAdmin], "active", Ct);
        await MemberRows.InsertAsync(db.AppConnectionString, beta.TenantId, "both", "both@beta.test", [TenantRoles.FinanceApprover], "active", Ct);
        await using var host = new ModuleHost(db.AppConnectionString, clock: new ManualClock());

        (await RolesAsync(host, acme, User("both", acme, email: null, verified: false))).ShouldBe([TenantRoles.TenantAdmin]);
        (await RolesAsync(host, beta, User("both", beta, email: null, verified: false))).ShouldBe([TenantRoles.FinanceApprover]);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private static ClaimsPrincipal User(string sub, TenantContext tenant, string? email, bool verified)
    {
        var claims = new List<Claim> { new("sub", sub), new("organization", tenant.KeycloakOrgAlias) };
        if (email is not null)
        {
            claims.Add(new Claim("email", email));
            claims.Add(new Claim("email_verified", verified ? "true" : "false", ClaimValueTypes.Boolean));
        }

        return AuthCookies.Principal(claims);
    }

    private static async Task<IReadOnlyList<string>> RolesAsync(ModuleHost host, TenantContext tenant, ClaimsPrincipal user)
    {
        await using var scope = host.ScopeFor(tenant);
        var transformed = await scope.ServiceProvider.GetRequiredService<IClaimsTransformation>().TransformAsync(user);
        return transformed.Identities
            .Where(i => i.AuthenticationType == MembersClaimsTransformation.AuthenticationType)
            .SelectMany(i => i.FindAll(IdentityClaims.Role))
            .Select(c => c.Value)
            .ToList();
    }
}
