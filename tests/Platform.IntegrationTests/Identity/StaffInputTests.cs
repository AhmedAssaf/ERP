using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Identity.Keycloak;
using Platform.Shared.Results;

namespace Platform.IntegrationTests.Identity;

/// <summary>
/// QA pass, F-06 negative inputs through <see cref="IStaffService"/>: an address, name or role the service must refuse
/// is refused with its validation code before any call to the Keycloak Admin API, and no member row is written. The
/// Admin API is a recording double that answers 503, so a value that slips through shows up as a recorded call.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class StaffInputTests(DatabaseFixture db)
{
    private const string Actor = "qa-input-admin";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("plain")]
    [InlineData("sara@")]
    [InlineData("@acme.example.sa")]
    [InlineData("sara ahmed@acme.example.sa")]
    [InlineData("Sara Ahmed <sara@acme.example.sa>")]
    [InlineData("sara@acme.example.sa\r\nBcc: someone@evil.example")]
    [InlineData("sara@acme.example.sa,omar@acme.example.sa")]
    [InlineData("sara@@acme.example.sa")]
    [InlineData("sara@acme..example.sa")]
    [InlineData("sara@acme.example.sa (comment)")]
    public async Task A_malformed_email_is_refused_before_keycloak_is_called(string email)
    {
        var (result, calls) = await InviteAsync(email, "Sara Ahmed", [TenantRoles.ContractsOfficer]);

        calls.ShouldBe(0, "the address reached the Keycloak Admin API");
        result.ShouldNotBeNull().Error!.Code.ShouldBe("identity.invalid_email");
    }

    [Fact]
    public async Task An_email_longer_than_254_characters_is_refused()
    {
        var email = new string('s', 64) + "@" + string.Join('.', Enumerable.Repeat(new string('a', 60), 3)) + ".example.sa";
        email.Length.ShouldBeGreaterThan(254);

        var (result, calls) = await InviteAsync(email, "Sara Ahmed", [TenantRoles.ContractsOfficer]);

        calls.ShouldBe(0);
        result.ShouldNotBeNull().Error!.Code.ShouldBe("identity.invalid_email");
    }

    [Theory]
    [InlineData("sara\u202E@acme.example.sa")]
    [InlineData("sara@acme\u200B.example.sa")]
    [InlineData("\u2066sara@acme.example.sa\u2069")]
    public async Task An_email_with_a_bidi_override_or_zero_width_character_is_refused(string email)
    {
        // The address is shown on /admin/staff and in emails like the name, so the characters the name refuses (they can
        // make a value display as something else) must not enter through the address either.
        var (result, calls) = await InviteAsync(email, "Sara Ahmed", [TenantRoles.ContractsOfficer]);

        calls.ShouldBe(0, "the address reached the Keycloak Admin API");
        result.ShouldNotBeNull().Error!.Code.ShouldBe("identity.invalid_email");
    }

    [Theory]
    [InlineData("Sara\u0000Ahmed")]
    [InlineData("Sara\u001BAhmed")]
    [InlineData("Sara\u007FAhmed")]
    [InlineData("Sara\u0085Ahmed")]
    [InlineData("Sara\u2028Ahmed")]
    [InlineData("\u061Cسارة أحمد")]
    [InlineData("Sara\u2060Ahmed")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("Sara Ahmed\r\nBcc: someone@evil.example")]
    public async Task A_name_with_a_control_format_or_markup_character_is_refused_before_keycloak_is_called(string name)
    {
        var (result, calls) = await InviteAsync($"qa.{Guid.NewGuid():N}@acme.example.sa", name, [TenantRoles.ContractsOfficer]);

        calls.ShouldBe(0);
        result.ShouldNotBeNull().Error!.Code.ShouldBe("identity.invalid_display_name");
    }

    [Fact]
    public async Task A_name_of_101_characters_is_refused()
    {
        var (result, calls) = await InviteAsync($"qa.{Guid.NewGuid():N}@acme.example.sa", new string('ع', 101), [TenantRoles.ContractsOfficer]);

        calls.ShouldBe(0);
        result.ShouldNotBeNull().Error!.Code.ShouldBe("identity.invalid_display_name");
    }

    [Fact]
    public async Task An_invitation_without_a_role_is_refused()
    {
        var (result, calls) = await InviteAsync($"qa.{Guid.NewGuid():N}@acme.example.sa", "Sara Ahmed", []);

        calls.ShouldBe(0);
        result.ShouldNotBeNull().Error!.Code.ShouldBe("identity.no_role");
    }

    [Theory]
    [InlineData("auditor")]
    [InlineData("platform-admin")]
    [InlineData("Tenant-Admin")]
    [InlineData("")]
    public async Task An_invitation_with_a_role_outside_the_four_tenant_roles_is_refused(string role)
    {
        var (result, calls) = await InviteAsync($"qa.{Guid.NewGuid():N}@acme.example.sa", "Sara Ahmed", [TenantRoles.ContractsOfficer, role]);

        calls.ShouldBe(0);
        result.ShouldNotBeNull().Error!.Code.ShouldBe("identity.unknown_role");
    }

    [Fact]
    public async Task No_member_row_is_written_for_a_refused_invitation()
    {
        var email = $"qa.{Guid.NewGuid():N}@acme.example.sa";

        var (result, _) = await InviteAsync(email, "Sara\u0000Ahmed", [TenantRoles.ContractsOfficer]);

        result.ShouldNotBeNull().IsSuccess.ShouldBeFalse();
        (await MemberRows.FindByEmailAsync(db.AppConnectionString, TestTenants.Acme.TenantId, email, Ct)).ShouldBeNull();
    }

    /// <summary>Invites as acme against the recording Admin API; a thrown exception means the call went out and failed.</summary>
    private async Task<(Result<Invitation>? Result, int Calls)> InviteAsync(string email, string name, string[] roles)
    {
        var recorder = new RecordingKeycloak();
        await using var host = new ModuleHost(db.AppConnectionString, Settings(), configure: services =>
            services.AddHttpClient<KeycloakAdminClient>().AddHttpMessageHandler(() => recorder));
        await using var scope = host.ScopeFor(TestTenants.Acme);
        try
        {
            return (await scope.ServiceProvider.GetRequiredService<IStaffService>().InviteAsync(email, name, roles, Actor, Ct), recorder.Calls);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !Ct.IsCancellationRequested)
        {
            return (null, recorder.Calls);
        }
    }

    private static IConfiguration Settings() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KeycloakAdmin:BaseUrl"] = "http://keycloak.invalid",
            ["KeycloakAdmin:ClientSecret"] = "unused",
            ["KeycloakAdmin:TenantUrl"] = "https://{slug}.localhost:8443/",
        })
        .Build();

    /// <summary>The Keycloak Admin API as a double: counts every request and answers 503 without any network.</summary>
    private sealed class RecordingKeycloak : DelegatingHandler
    {
        private int _calls;

        public int Calls => _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }
}
