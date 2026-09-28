using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Tenancy.Contracts;
using Platform.Shared.Tenancy;
using Platform.UI;
using SkiaSharp;

namespace Platform.IntegrationTests.Tenancy;

/// <summary>
/// F-02 as narrowed (docs/05 row 2, spec 4.3, D-9, D-11) through <see cref="IBrandingService"/>: a tenant brands only
/// itself through <c>tenancy.update_branding</c>, a colour under 4.5:1 on white is stored darkened and reported, the logo
/// is re-encoded into object storage under the tenant's prefix, and every change is audited.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class BrandingServiceTests(DatabaseFixture db, MinioFixture minio) : IClassFixture<MinioFixture>
{
    private const string Actor = "branding-admin";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Saving_branding_changes_only_the_current_tenant()
    {
        var mine = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        var other = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        await using var host = Host();

        var saved = await SaveAsync(host, mine, "  Mine Trading  ", "#1e4e79");

        saved.IsSuccess.ShouldBeTrue();
        saved.Value.Branding.ShouldBe(new TenantBranding("Mine Trading", "#1E4E79", null));
        saved.Value.ColorAdjusted.ShouldBeFalse();
        (await TenantRows.BrandingAsync(db.AppConnectionString, mine, Ct)).ShouldBe(new TenantBranding("Mine Trading", "#1E4E79", null));
        (await TenantRows.BrandingAsync(db.AppConnectionString, other, Ct)).ShouldBe(other.Branding);
        (await MemberRows.AuditCountAsync(db.OwnerConnectionString, mine.TenantId, Actor, "tenancy.branding_changed", Ct)).ShouldBe(1);
        (await MemberRows.AuditCountAsync(db.OwnerConnectionString, other.TenantId, Actor, "tenancy.branding_changed", Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task The_branding_function_writes_nothing_without_a_tenant_and_only_the_connections_tenant_with_one()
    {
        var mine = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        var other = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        await using var connection = new NpgsqlConnection(db.AppConnectionString);
        await connection.OpenAsync(Ct);

        await using (var none = new NpgsqlCommand("select count(*) from tenancy.update_branding('Nobody', '#000000', null)", connection))
        {
            (await none.ExecuteScalarAsync(Ct)).ShouldBe(0L);
        }

        // As an active tenant admin of that tenant (tenancy migration 0007).
        await MemberRows.EnsureActiveAdminAsync(db.OwnerConnectionString, mine.TenantId, Actor, Ct);
        await using (var scoped = new NpgsqlCommand("""
            select set_config('app.tenant_id', @tenant, false), set_config('app.user_id', @user, false);
            select count(*) from tenancy.update_branding('Scoped', '#000000', null);
            """, connection))
        {
            scoped.Parameters.AddWithValue("tenant", mine.TenantId.ToString());
            scoped.Parameters.AddWithValue("user", Actor);
            await scoped.ExecuteNonQueryAsync(Ct);
        }

        (await TenantRows.BrandingAsync(db.AppConnectionString, mine, Ct)).PortalName.ShouldBe("Scoped");
        (await TenantRows.BrandingAsync(db.AppConnectionString, other, Ct)).ShouldBe(other.Branding);

        // The table itself stays unwritable by the app role.
        await using var direct = new NpgsqlCommand($"update tenancy.tenants set portal_name = 'x' where id = '{other.TenantId}'", connection);
        (await Should.ThrowAsync<PostgresException>(() => direct.ExecuteNonQueryAsync(Ct))).SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task A_low_contrast_colour_is_stored_darkened_and_reported()
    {
        var tenant = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        await using var host = Host();

        var saved = await SaveAsync(host, tenant, "Lemon", "#FFFACD");

        saved.IsSuccess.ShouldBeTrue();
        saved.Value.RequestedColor.ShouldBe("#FFFACD");
        saved.Value.ColorAdjusted.ShouldBeTrue();
        saved.Value.Branding.PrimaryColor.ShouldBe(BrandColors.EnsureContrast("#FFFACD", 4.5));
        BrandColors.ContrastWithWhite(saved.Value.Branding.PrimaryColor).ShouldBeGreaterThanOrEqualTo(4.5);
        (await TenantRows.BrandingAsync(db.AppConnectionString, tenant, Ct)).PrimaryColor.ShouldBe(saved.Value.Branding.PrimaryColor);
    }

    [Theory]
    [InlineData("", "#0F766E", "tenancy.invalid_portal_name")]
    [InlineData("   ", "#0F766E", "tenancy.invalid_portal_name")]
    [InlineData("Name\u0007", "#0F766E", "tenancy.invalid_portal_name")]
    [InlineData("Name", "#12345", "tenancy.invalid_color")]
    [InlineData("Name", "red", "tenancy.invalid_color")]
    [InlineData("Name", "#0F766E;}", "tenancy.invalid_color")]
    public async Task Invalid_branding_is_refused_and_nothing_changes(string name, string colour, string code)
    {
        var tenant = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        await using var host = Host();

        var saved = await SaveAsync(host, tenant, name, colour);

        saved.IsSuccess.ShouldBeFalse();
        saved.Error.Code.ShouldBe(code);
        (await TenantRows.BrandingAsync(db.AppConnectionString, tenant, Ct)).ShouldBe(tenant.Branding);
    }

    [Fact]
    public async Task A_portal_name_over_100_characters_is_refused()
    {
        var tenant = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        await using var host = Host();

        (await SaveAsync(host, tenant, new string('x', 101), "#0F766E")).Error!.Code.ShouldBe("tenancy.invalid_portal_name");
        (await SaveAsync(host, tenant, new string('x', 100), "#0F766E")).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task The_host_cache_shows_the_new_branding_after_a_save()
    {
        var tenant = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        await using var host = Host();
        var directory = host.Services.GetRequiredService<ITenantDirectory>();
        (await directory.FindByHostAsync(TenantRows.Host(tenant), Ct))!.Branding.PortalName.ShouldBe(tenant.Branding.PortalName);

        (await SaveAsync(host, tenant, "Renamed", "#1E4E79")).IsSuccess.ShouldBeTrue();

        (await directory.FindByHostAsync(TenantRows.Host(tenant), Ct))!.Branding.ShouldBe(new TenantBranding("Renamed", "#1E4E79", null));
    }

    [Fact]
    public async Task A_logo_is_reencoded_into_the_tenants_prefix_and_readable_only_by_that_tenant()
    {
        var tenant = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        var other = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        await using var host = Host();

        TenantBranding branding;
        await MemberRows.EnsureActiveAdminAsync(db.OwnerConnectionString, tenant.TenantId, Actor, Ct);
        await using (var scope = host.ScopeFor(tenant, actingUserId: Actor))
        {
            await using var upload = new MemoryStream(Jpeg(1600, 800));
            var saved = await scope.ServiceProvider.GetRequiredService<IBrandingService>().SaveLogoAsync(upload, "image/jpeg", Actor, Ct);
            saved.IsSuccess.ShouldBeTrue();
            branding = saved.Value;
        }

        branding.LogoUrl.ShouldNotBeNull().ShouldMatch("^/branding/logo/[a-f0-9]{64}\\.png$");
        var hash = branding.LogoUrl["/branding/logo/".Length..^".png".Length];
        (await TenantRows.BrandingAsync(db.AppConnectionString, tenant, Ct)).LogoUrl.ShouldBe(branding.LogoUrl);

        var stored = (await minio.ReadAsync($"tenants/{tenant.TenantId}/branding/logo-{hash}.png", Ct)).ShouldNotBeNull();
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stored)).ShouldBe(hash);
        using (var decoded = SKBitmap.Decode(stored))
        {
            (decoded.Width, decoded.Height).ShouldBe((1024, 512));
        }

        await using (var scope = host.ScopeFor(tenant))
        {
            await using var logo = await scope.ServiceProvider.GetRequiredService<IBrandingService>().OpenLogoAsync(hash, Ct);
            logo.ShouldNotBeNull();
        }

        await using (var scope = host.ScopeFor(other))
        {
            (await scope.ServiceProvider.GetRequiredService<IBrandingService>().OpenLogoAsync(hash, Ct)).ShouldBeNull();
        }

        (await MemberRows.AuditCountAsync(db.OwnerConnectionString, tenant.TenantId, Actor, "tenancy.branding_changed", Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Saving_as_someone_other_than_the_acting_user_is_a_defect()
    {
        // The database checks the acting user of the session (app.user_id), which comes from the request or circuit's
        // principal, never from a parameter (vendor spec section 2); an actorId that differs is a caller's bug.
        var tenant = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        await MemberRows.EnsureActiveAdminAsync(db.OwnerConnectionString, tenant.TenantId, Actor, Ct);
        await using var host = Host();
        await using var scope = host.ScopeFor(tenant, actingUserId: "someone-else");
        var branding = scope.ServiceProvider.GetRequiredService<IBrandingService>();

        await Should.ThrowAsync<InvalidOperationException>(() => branding.SaveAsync("Mismatch", "#1E4E79", Actor, Ct));
        await using (var upload = new MemoryStream(Jpeg(200, 100)))
        {
            await Should.ThrowAsync<InvalidOperationException>(() => branding.SaveLogoAsync(upload, "image/jpeg", Actor, Ct));
        }

        (await TenantRows.BrandingAsync(db.AppConnectionString, tenant, Ct)).ShouldBe(tenant.Branding);
        (await MemberRows.AuditCountAsync(db.OwnerConnectionString, tenant.TenantId, Actor, "tenancy.branding_changed", Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Saving_without_an_acting_user_is_a_defect()
    {
        var tenant = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        await MemberRows.EnsureActiveAdminAsync(db.OwnerConnectionString, tenant.TenantId, Actor, Ct);
        await using var host = Host();
        await using var scope = host.ScopeFor(tenant);

        await Should.ThrowAsync<InvalidOperationException>(
            () => scope.ServiceProvider.GetRequiredService<IBrandingService>().SaveAsync("Nobody", "#1E4E79", Actor, Ct));

        (await TenantRows.BrandingAsync(db.AppConnectionString, tenant, Ct)).ShouldBe(tenant.Branding);
    }

    [Fact]
    public async Task An_acting_user_who_is_not_an_active_tenant_admin_is_not_allowed_on_either_path()
    {
        var tenant = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        const string stranger = "branding-stranger";
        await using var host = Host();
        await using var scope = host.ScopeFor(tenant, actingUserId: stranger);
        var branding = scope.ServiceProvider.GetRequiredService<IBrandingService>();

        (await branding.SaveAsync("Stranger", "#1E4E79", stranger, Ct)).Error.ShouldNotBeNull().Code.ShouldBe(BrandingErrors.NotAllowed);
        await using (var upload = new MemoryStream(Jpeg(200, 100)))
        {
            (await branding.SaveLogoAsync(upload, "image/jpeg", stranger, Ct)).Error.ShouldNotBeNull().Code.ShouldBe(BrandingErrors.NotAllowed);
        }

        (await TenantRows.BrandingAsync(db.AppConnectionString, tenant, Ct)).ShouldBe(tenant.Branding);
    }

    [Fact]
    public async Task A_tenant_admin_who_is_also_a_vendor_user_is_not_allowed_with_or_without_a_vendor_context()
    {
        // In a circuit the vendor context may be set (name and colour); the logo POST has none. Both refuse the same way:
        // a vendor user never brands a tenant (V-3: vendors never hold staff rows; the same rule as approve_relationship).
        var tenant = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        var dualUser = $"dual-{Guid.NewGuid():N}";
        var companyId = await VendorRows.RegisterAsync(db.AppConnectionString, tenant, dualUser, VendorRows.NewCrNumber(), "Dual Branding Company", Ct);
        await MemberRows.EnsureActiveAdminAsync(db.OwnerConnectionString, tenant.TenantId, dualUser, Ct);
        await using var host = Host();

        await using (var circuit = host.ScopeFor(tenant, companyId, dualUser))
        {
            var saved = await circuit.ServiceProvider.GetRequiredService<IBrandingService>().SaveAsync("Dual", "#1E4E79", dualUser, Ct);
            saved.Error.ShouldNotBeNull().Code.ShouldBe(BrandingErrors.NotAllowed);
        }

        await using (var request = host.ScopeFor(tenant, actingUserId: dualUser))
        {
            var branding = request.ServiceProvider.GetRequiredService<IBrandingService>();
            (await branding.SaveAsync("Dual", "#1E4E79", dualUser, Ct)).Error.ShouldNotBeNull().Code.ShouldBe(BrandingErrors.NotAllowed);
            await using var upload = new MemoryStream(Jpeg(200, 100));
            (await branding.SaveLogoAsync(upload, "image/jpeg", dualUser, Ct)).Error.ShouldNotBeNull().Code.ShouldBe(BrandingErrors.NotAllowed);
        }

        (await TenantRows.BrandingAsync(db.AppConnectionString, tenant, Ct)).ShouldBe(tenant.Branding);
        (await MemberRows.AuditCountAsync(db.OwnerConnectionString, tenant.TenantId, dualUser, "tenancy.branding_changed", Ct)).ShouldBe(0);
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("ABCDEF")]
    [InlineData("")]
    public async Task A_logo_name_that_is_not_a_hash_is_never_looked_up(string hash)
    {
        var tenant = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        await using var host = Host();
        await using var scope = host.ScopeFor(tenant);

        (await scope.ServiceProvider.GetRequiredService<IBrandingService>().OpenLogoAsync(hash, Ct)).ShouldBeNull();
    }

    [Theory]
    [InlineData("/branding/logo/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef.png", true)]
    [InlineData("https://cdn.example.sa/logo.png", true)]
    [InlineData("/branding/logo/../../admin.png", false)]
    [InlineData("/branding/logo/0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF.png", false)]
    [InlineData("//evil.example/logo.png", false)]
    [InlineData("javascript:alert(1)", false)]
    public async Task The_logo_url_constraint_allows_https_and_the_app_path_only(string url, bool allowed)
    {
        await using var connection = new NpgsqlConnection(db.OwnerConnectionString);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using var command = new NpgsqlCommand("""
            insert into tenancy.tenants (id, slug, keycloak_org_alias, portal_name, primary_color, logo_url)
            values (gen_random_uuid(), 'logo-rule', 'logo-rule', 'Logo Rule', '#000000', @url)
            """, connection, transaction);
        command.Parameters.AddWithValue("url", url);

        if (allowed)
        {
            (await command.ExecuteNonQueryAsync(Ct)).ShouldBe(1);
        }
        else
        {
            (await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct))).ConstraintName.ShouldBe("ck_tenants_logo_url");
        }

        await transaction.RollbackAsync(Ct);
    }

    private ModuleHost Host() =>
        new(db.AppConnectionString, objectStorage: new ConfigurationBuilder().AddInMemoryCollection(minio.Settings).Build());

    private async Task<Platform.Shared.Results.Result<BrandingSaved>> SaveAsync(ModuleHost host, TenantContext tenant, string name, string colour)
    {
        await MemberRows.EnsureActiveAdminAsync(db.OwnerConnectionString, tenant.TenantId, Actor, Ct);
        await using var scope = host.ScopeFor(tenant, actingUserId: Actor);
        return await scope.ServiceProvider.GetRequiredService<IBrandingService>().SaveAsync(name, colour, Actor, Ct);
    }

    internal static byte[] Jpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(0x9A, 0x34, 0x12));
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }
}
