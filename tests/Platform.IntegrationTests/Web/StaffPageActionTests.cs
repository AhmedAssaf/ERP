using System.Globalization;
using System.Security.Claims;
using Bunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;
using Platform.Shared.Results;
using Platform.UI;
using Platform.Web.Components.Pages.Admin;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// <c>/admin/staff</c> actions (F-06, spec 4.2) driven through the component with bUnit: invite, change roles and resend
/// go through the page's buttons and dialogs, each re-checking the admin role in <c>identity.members</c> (the real
/// directory on the test database) at the moment it runs. Every successful invitation shows the same neutral message;
/// validation errors land on their fields; Keycloak unreachable shows the generic error and saves nothing, and the page
/// logs the exception type only.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class StaffPageActionTests(DatabaseFixture db) : IDisposable
{
    private const string NotAllowed = "Only a tenant administrator can do this.";
    private const string Generic = "The change could not be completed. Try again in a moment.";

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private readonly BunitContext _page = new();
    private readonly CapturingLogger _log = new();
    private readonly string _admin = $"admin-{Guid.NewGuid():N}";

    [Fact]
    public async Task Invite_change_roles_and_resend_run_through_the_page()
    {
        await AdminRowAsync();
        var invitee = Member($"invitee-{Guid.NewGuid():N}@acme.test");
        var staff = new FakeStaff([invitee]);
        await using var host = new ModuleHost(db.AppConnectionString);
        await using var scope = host.ScopeFor(TestTenants.Acme);
        var page = Render(scope, staff);

        await InviteAsync(page, "new.person@acme.test", "New Person", TenantRoles.ContractsOfficer);
        page.WaitForAssertion(() => page.Markup.ShouldContain(InvitedTo("new.person@acme.test")), RenderWait.Timeout);
        staff.Invites.ShouldHaveSingleItem().ShouldBe(("new.person@acme.test", "New Person", _admin));

        await page.Find($"[data-change-roles='{invitee.UserId}']").ClickAsync(new());
        await page.Find($"[role=dialog] [data-role='{TenantRoles.FinanceApprover}']").ChangeAsync(new() { Value = true });
        await ConfirmAsync(page, "Save roles");
        page.WaitForAssertion(() => page.Markup.ShouldContain($"Roles saved for {invitee.DisplayName}"), RenderWait.Timeout);
        staff.RoleChanges.ShouldHaveSingleItem().ShouldBe((invitee.UserId!, $"{TenantRoles.ContractsOfficer},{TenantRoles.FinanceApprover}", _admin));

        await page.Find($"[data-resend='{invitee.UserId}']").ClickAsync(new());
        page.WaitForAssertion(() => page.Markup.ShouldContain($"Invitation sent again to {invitee.Email}"), RenderWait.Timeout);
        staff.Resends.ShouldHaveSingleItem().ShouldBe((invitee.UserId!, _admin));
    }

    [Fact]
    public async Task A_new_and_an_existing_account_get_the_same_invitation_message()
    {
        await AdminRowAsync();
        var staff = new FakeStaff([]);
        await using var host = new ModuleHost(db.AppConnectionString);
        await using var scope = host.ScopeFor(TestTenants.Acme);
        var page = Render(scope, staff);

        await InviteAsync(page, "first@acme.test", "First Person", TenantRoles.ContractsOfficer);
        page.WaitForAssertion(() => page.Markup.ShouldContain(InvitedTo("first@acme.test")), RenderWait.Timeout);
        await InviteAsync(page, "second@acme.test", "Second Person", TenantRoles.ContractsOfficer);
        page.WaitForAssertion(() => page.Markup.ShouldContain(InvitedTo("second@acme.test")), RenderWait.Timeout);

        page.Markup.ShouldNotContain("already have an account");
    }

    [Fact]
    public async Task An_admin_demoted_while_the_page_is_open_is_refused_every_action()
    {
        await AdminRowAsync();
        var invitee = Member($"invitee-{Guid.NewGuid():N}@acme.test");
        var staff = new FakeStaff([invitee]);
        await using var host = new ModuleHost(db.AppConnectionString);
        await using var scope = host.ScopeFor(TestTenants.Acme);
        var page = Render(scope, staff);
        await MemberRows.OverwriteRolesAsync(db.AppConnectionString, TestTenants.Acme.TenantId, _admin, [TenantRoles.ContractsOfficer], Ct);

        await InviteAsync(page, "new.person@acme.test", "New Person", TenantRoles.ContractsOfficer);
        page.WaitForAssertion(() => page.Find("[role=dialog] [role=alert]").TextContent.ShouldBe(NotAllowed), RenderWait.Timeout);
        await page.Find("[role=dialog]").QuerySelectorAll("button").First(b => b.TextContent.Contains("Cancel", StringComparison.Ordinal)).ClickAsync(new());

        await page.Find($"[data-change-roles='{invitee.UserId}']").ClickAsync(new());
        await ConfirmAsync(page, "Save roles");
        page.WaitForAssertion(() => page.Find("[role=dialog]").TextContent.ShouldContain(NotAllowed), RenderWait.Timeout);
        await page.Find("[role=dialog]").QuerySelectorAll("button").First(b => b.TextContent.Contains("Cancel", StringComparison.Ordinal)).ClickAsync(new());

        await page.Find($"[data-resend='{invitee.UserId}']").ClickAsync(new());
        page.WaitForAssertion(() => page.Markup.ShouldContain(NotAllowed), RenderWait.Timeout);

        staff.Invites.ShouldBeEmpty();
        staff.RoleChanges.ShouldBeEmpty();
        staff.Resends.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_invalid_email_or_name_shows_the_field_message()
    {
        await AdminRowAsync();
        await using var host = new ModuleHost(db.AppConnectionString, UnreachableKeycloak());
        await using var scope = host.ScopeFor(TestTenants.Acme);
        var page = Render(scope, scope.ServiceProvider.GetRequiredService<IStaffService>());

        await InviteAsync(page, "not-an-email", "Valid Name", TenantRoles.ContractsOfficer);
        page.WaitForAssertion(() => page.Find("[role=dialog]").TextContent
            .ShouldContain("Enter the person's work email address, for example name@company.com."), RenderWait.Timeout);

        await page.Find("[role=dialog] input[type=email]").ChangeAsync(new() { Value = "valid.person@acme.test" });
        await page.Find("[role=dialog] input[type=text]").ChangeAsync(new() { Value = "<b>Bold</b>" });
        await ConfirmAsync(page, "Send invitation");
        page.WaitForAssertion(() => page.Find("[role=dialog]").TextContent
            .ShouldContain("Enter the person's full name, up to 100 characters, using letters, spaces, apostrophes, hyphens and periods."), RenderWait.Timeout);
        page.Find("[role=dialog]").TextContent.ShouldNotContain("Enter the person's work email address");
    }

    [Fact]
    public async Task Keycloak_unreachable_shows_the_generic_error_saves_no_member_and_logs_the_type_only()
    {
        await AdminRowAsync();
        var email = $"unreachable-{Guid.NewGuid():N}@acme.test";
        await using var host = new ModuleHost(db.AppConnectionString, UnreachableKeycloak());
        await using var scope = host.ScopeFor(TestTenants.Acme);
        var page = Render(scope, scope.ServiceProvider.GetRequiredService<IStaffService>());

        await InviteAsync(page, email, "Nobody Home", TenantRoles.ContractsOfficer);

        page.WaitForAssertion(() => page.Find("[role=dialog] [role=alert]").TextContent.ShouldBe(Generic), TimeSpan.FromSeconds(20));
        (await MemberRows.FindByEmailAsync(db.AppConnectionString, TestTenants.Acme.TenantId, email, Ct)).ShouldBeNull();
        var entry = _log.Entries.ShouldHaveSingleItem();
        entry.Exception.ShouldBeNull("the page logs the exception type, never the exception or its message");
        // Windows retries a refused connection until the client's timeout, so the type is either of the two.
        entry.Message.ShouldMatch(@"\((HttpRequestException|TaskCanceledException)\)\.$");
        entry.Message.ShouldNotContain("127.0.0.1");
    }

    public void Dispose() => _page.Dispose();

    private IRenderedComponent<Staff> Render(AsyncServiceScope scope, IStaffService staff)
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        _page.Services.AddLocalization(o => o.ResourcesPath = "Resources");
        _page.Services.AddPlatformUI();
        _page.Services.AddSingleton(staff);
        _page.Services.AddSingleton(scope.ServiceProvider.GetRequiredService<IMemberDirectory>());
        _page.Services.AddSingleton<ILogger<Staff>>(_log);
        _page.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = _page.AddAuthorization();
        auth.SetAuthorized(_admin);
        auth.SetClaims(new Claim(IdentityClaims.Subject, _admin));
        auth.SetPolicies(TenantPolicies.TenantAdmin);
        var page = _page.Render<Staff>();
        page.WaitForAssertion(() => page.Markup.ShouldNotContain("Loading staff"), RenderWait.Timeout);
        return page;
    }

    private static async Task InviteAsync(IRenderedComponent<Staff> page, string email, string name, string role)
    {
        await page.Find("[data-invite]").ClickAsync(new());
        await page.Find("[role=dialog] input[type=email]").ChangeAsync(new() { Value = email });
        await page.Find("[role=dialog] input[type=text]").ChangeAsync(new() { Value = name });
        await page.Find($"[role=dialog] [data-role='{role}']").ChangeAsync(new() { Value = true });
        await ConfirmAsync(page, "Send invitation");
    }

    private static string InvitedTo(string email) => $"Invitation sent to {email}";

    private static Task ConfirmAsync(IRenderedComponent<Staff> page, string text) =>
        page.Find("[role=dialog]").QuerySelectorAll("button").Single(b => b.TextContent.Trim() == text).ClickAsync(new());

    private Task AdminRowAsync() =>
        MemberRows.InsertAsync(db.AppConnectionString, TestTenants.Acme.TenantId, _admin, $"{_admin}@acme.test", [TenantRoles.TenantAdmin], "active", Ct);

    // Nothing listens on the discard port, so every Admin API call fails to connect.
    private static IConfiguration UnreachableKeycloak() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KeycloakAdmin:BaseUrl"] = "http://127.0.0.1:9",
            ["KeycloakAdmin:ClientSecret"] = "unused",
            ["KeycloakAdmin:TenantUrl"] = "https://{slug}.localhost:8443/",
        })
        .Build();

    private static Member Member(string email) => new(
        $"user-{Guid.NewGuid():N}", email, "Invited Person", [TenantRoles.ContractsOfficer], MemberStatus.Invited, DateTimeOffset.UtcNow, null);

    /// <summary>The staff service behind the page, recording what the page asked of it.</summary>
    private sealed class FakeStaff(IReadOnlyList<Member> members) : IStaffService
    {
        public List<(string Email, string Name, string Actor)> Invites { get; } = [];

        public List<(string UserId, string Roles, string Actor)> RoleChanges { get; } = [];

        public List<(string UserId, string Actor)> Resends { get; } = [];

        public Task<IReadOnlyList<Member>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult(members);

        public Task<Result<Invitation>> InviteAsync(
            string email, string displayName, IReadOnlyCollection<string> roles, string actorId, CancellationToken cancellationToken = default)
        {
            Invites.Add((email, displayName, actorId));
            var member = new Member($"user-{Guid.NewGuid():N}", email, displayName, [.. roles], MemberStatus.Invited, DateTimeOffset.UtcNow, null);
            return Task.FromResult(Result.Success(new Invitation(member, InvitationEmail.Sent)));
        }

        public Task<Result<Member>> SetRolesAsync(
            string userId, IReadOnlyCollection<string> roles, string actorId, CancellationToken cancellationToken = default)
        {
            RoleChanges.Add((userId, string.Join(",", TenantRoles.All.Where(roles.Contains)), actorId));
            var member = members.Single(m => m.UserId == userId);
            return Task.FromResult(Result.Success(member with { Roles = [.. roles] }));
        }

        public Task<Result<Invitation>> ResendAsync(string userId, string actorId, CancellationToken cancellationToken = default)
        {
            Resends.Add((userId, actorId));
            return Task.FromResult(Result.Success(new Invitation(members.Single(m => m.UserId == userId), InvitationEmail.Sent)));
        }
    }

    private sealed class CapturingLogger : ILogger<Staff>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
