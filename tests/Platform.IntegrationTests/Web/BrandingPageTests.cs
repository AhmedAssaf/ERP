using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Platform.IntegrationTests.Infrastructure;
using Platform.IntegrationTests.Tenancy;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Tenancy.Contracts;
using Platform.Shared.Tenancy;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// F-02 through the web host (spec 4.3, D-10): <c>/admin/branding</c> and its logo endpoint for tenant admins only, the
/// upload re-encoded and served from <c>/branding/logo/{hash}.png</c> for that tenant's host only, with immutable cache
/// headers, and the header showing the new name and logo on the next page load.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class BrandingPageTests(DatabaseFixture db, MinioFixture minio) : IClassFixture<MinioFixture>
{
    private const string LogoPath = "/admin/branding/logo";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_non_admin_cannot_open_the_branding_page_or_upload_a_logo()
    {
        var (tenant, _) = await TenantWithAdminAsync();
        var evaluator = new TestUser($"evaluator-{Guid.NewGuid():N}", [tenant.KeycloakOrgAlias]);
        await MemberRows.InsertAsync(db.AppConnectionString, tenant.TenantId, evaluator.Subject, $"{evaluator.Subject}@t.test", [TenantRoles.TechnicalEvaluator], "active", Ct);
        await using var factory = Factory();
        using var client = Client(factory, TenantRows.Host(tenant));

        using var page = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/admin/branding").As(evaluator), Ct);
        using var upload = await PostLogoAsync(factory, client, evaluator, Png(), "logo.png", "image/png");

        page.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        upload.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await TenantRows.BrandingAsync(db.AppConnectionString, tenant, Ct)).LogoUrl.ShouldBeNull();
    }

    [Fact]
    public async Task A_tenant_admin_sees_the_branding_form_with_a_plain_multipart_logo_form()
    {
        var (tenant, admin) = await TenantWithAdminAsync();
        await using var factory = Factory();
        using var client = Client(factory, TenantRows.Host(tenant));

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/admin/branding").As(admin), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(Ct);
        html.ShouldContain("data-branding-form");
        html.ShouldContain($"action=\"{LogoPath}\"");
        html.ShouldContain("enctype=\"multipart/form-data\"");
        html.ShouldContain("accept=\"image/png,image/jpeg\"");
        html.ShouldContain("__RequestVerificationToken");
    }

    [Fact]
    public async Task A_logo_upload_is_reencoded_and_served_for_that_tenant_only()
    {
        var (tenant, admin) = await TenantWithAdminAsync();
        var other = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        await using var factory = Factory();
        using var client = Client(factory, TenantRows.Host(tenant));

        using var upload = await PostLogoAsync(factory, client, admin, BrandingServiceTests.Jpeg(2000, 1000), "logo.jpg", "image/jpeg");

        upload.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        upload.Headers.Location!.OriginalString.ShouldBe("/admin/branding?logo=saved");
        var logoUrl = (await TenantRows.BrandingAsync(db.AppConnectionString, tenant, Ct)).LogoUrl.ShouldNotBeNull();
        logoUrl.ShouldMatch("^/branding/logo/[a-f0-9]{64}\\.png$");

        // Anonymous on the tenant's own host: the sign-in page and emails show it before anyone signs in.
        using var anonymous = Client(factory, TenantRows.Host(tenant));
        using var served = await anonymous.GetAsync(logoUrl, Ct);
        served.StatusCode.ShouldBe(HttpStatusCode.OK);
        served.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
        served.Headers.CacheControl!.ToString().ShouldBe("public, max-age=31536000, immutable");
        served.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
        var bytes = await served.Content.ReadAsByteArrayAsync(Ct);
        using (var decoded = SkiaSharp.SKBitmap.Decode(bytes))
        {
            (decoded.Width, decoded.Height).ShouldBe((1024, 512));
        }

        using var elsewhere = Client(factory, TenantRows.Host(other));
        (await elsewhere.GetAsync(logoUrl, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await anonymous.GetAsync("/branding/logo/" + new string('0', 64) + ".png", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await anonymous.GetAsync("/branding/logo/not-a-hash.png", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await MemberRows.AuditCountAsync(db.OwnerConnectionString, tenant.TenantId, admin.Subject, "tenancy.branding_changed", Ct)).ShouldBe(1);
    }

    [Theory]
    [InlineData("svg", "logo=not-image")]
    [InlineData("text", "logo=not-image")]
    [InlineData("oversized", "logo=too-large")]
    [InlineData("huge-dimensions", "logo=too-many-pixels")]
    [InlineData("none", "logo=missing")]
    public async Task A_non_image_or_oversized_upload_is_rejected(string kind, string outcome)
    {
        var (tenant, admin) = await TenantWithAdminAsync();
        await using var factory = Factory();
        using var client = Client(factory, TenantRows.Host(tenant));
        var (content, name, type) = kind switch
        {
            "svg" => ("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8.ToArray(), "logo.svg", "image/svg+xml"),
            "text" => ("hello"u8.ToArray(), "logo.png", "image/png"),
            "oversized" => (new byte[600 * 1024], "logo.png", "image/png"),
            "huge-dimensions" => (Png(5000, 10), "logo.png", "image/png"),
            _ => (Array.Empty<byte>(), string.Empty, "application/octet-stream"),
        };

        using var response = await PostLogoAsync(factory, client, admin, content, name, type);

        response.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        response.Headers.Location!.OriginalString.ShouldBe($"/admin/branding?{outcome}");
        (await TenantRows.BrandingAsync(db.AppConnectionString, tenant, Ct)).LogoUrl.ShouldBeNull();
    }

    [Fact]
    public async Task An_upload_without_an_antiforgery_token_is_refused()
    {
        var (tenant, admin) = await TenantWithAdminAsync();
        await using var factory = Factory();
        using var client = Client(factory, TenantRows.Host(tenant));
        using var form = new MultipartFormDataContent { { File(Png(), "image/png"), "logo", "logo.png" } };

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, LogoPath) { Content = form }.As(admin), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await TenantRows.BrandingAsync(db.AppConnectionString, tenant, Ct)).LogoUrl.ShouldBeNull();
    }

    [Fact]
    public async Task An_admin_whose_role_was_removed_cannot_upload()
    {
        var (tenant, admin) = await TenantWithAdminAsync();
        await using var factory = Factory();
        using var client = Client(factory, TenantRows.Host(tenant));
        // The role policy passes on cached roles (up to 30 s); the endpoint checks identity.members again before acting.
        using (var warm = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/admin/branding").As(admin), Ct))
        {
            warm.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await MemberRows.OverwriteRolesAsync(db.AppConnectionString, tenant.TenantId, admin.Subject, [TenantRoles.ContractsOfficer], Ct);
        using var response = await PostLogoAsync(factory, client, admin, Png(), "logo.png", "image/png");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await TenantRows.BrandingAsync(db.AppConnectionString, tenant, Ct)).LogoUrl.ShouldBeNull();
    }

    [Fact]
    public async Task The_header_shows_the_new_logo_and_name_after_saving()
    {
        var (tenant, admin) = await TenantWithAdminAsync();
        await using var factory = Factory();
        using var client = Client(factory, TenantRows.Host(tenant));
        (await PageAsync(client, admin)).ShouldContain(tenant.Branding.PortalName);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantAccessor>().Set(tenant);
            var saved = await scope.ServiceProvider.GetRequiredService<IBrandingService>().SaveAsync("Renamed Portal", "#FFFACD", admin.Subject, Ct);
            saved.IsSuccess.ShouldBeTrue();
        }

        using (var upload = await PostLogoAsync(factory, client, admin, Png(), "logo.png", "image/png"))
        {
            upload.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        }

        var stored = await TenantRows.BrandingAsync(db.AppConnectionString, tenant, Ct);
        var html = await PageAsync(client, admin);
        var header = html[html.IndexOf("<header", StringComparison.Ordinal)..html.IndexOf("</header>", StringComparison.Ordinal)];
        header.ShouldContain("Renamed Portal");
        header.ShouldContain($"src=\"{stored.LogoUrl}\"");
        html.ShouldContain($"--color-primary: {stored.PrimaryColor}");
        stored.PrimaryColor.ShouldNotBe("#FFFACD");
    }

    private async Task<(TenantContext Tenant, TestUser Admin)> TenantWithAdminAsync()
    {
        var tenant = await TenantRows.InsertAsync(db.OwnerConnectionString, Ct);
        var admin = new TestUser($"admin-{Guid.NewGuid():N}", [tenant.KeycloakOrgAlias], "en");
        await MemberRows.InsertAsync(db.AppConnectionString, tenant.TenantId, admin.Subject, $"{admin.Subject}@t.test", [TenantRoles.TenantAdmin], "active", Ct);
        return (tenant, admin);
    }

    private WebApplicationFactory<Program> Factory() =>
        new PlatformWebFactory(db.AppConnectionString).WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in minio.Settings)
            {
                builder.UseSetting(key, value);
            }
        });

    private static HttpClient Client(WebApplicationFactory<Program> factory, string host) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://{host}"), AllowAutoRedirect = false, HandleCookies = false });

    private static async Task<string> PageAsync(HttpClient client, TestUser user)
    {
        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/admin/branding").As(user), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync(Ct);
    }

    /// <summary>Posts the logo form as the page would: multipart, the antiforgery field and cookie, the signed-in user.</summary>
    private static async Task<HttpResponseMessage> PostLogoAsync(
        WebApplicationFactory<Program> factory, HttpClient client, TestUser user, byte[] content, string fileName, string contentType)
    {
        var tokens = AntiforgeryTokens(factory.Services, user);
        using var form = new MultipartFormDataContent { { new StringContent(tokens.RequestToken), tokens.FormField } };
        if (fileName.Length > 0)
        {
            form.Add(File(content, contentType), "logo", fileName);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, LogoPath) { Content = form }.As(user);
        request.Headers.Add("Cookie", $"{tokens.CookieName}={tokens.CookieToken}");
        return await client.SendAsync(request, Ct);
    }

    private static ByteArrayContent File(byte[] content, string contentType)
    {
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return file;
    }

    private static (string CookieName, string CookieToken, string FormField, string RequestToken) AntiforgeryTokens(
        IServiceProvider services, TestUser user)
    {
        using var scope = services.CreateScope();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", user.Subject)], TestAuthHandler.SchemeName));
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider, User = principal };
        var tokens = scope.ServiceProvider.GetRequiredService<IAntiforgery>().GetTokens(context);
        var cookieName = services.GetRequiredService<IOptions<AntiforgeryOptions>>().Value.Cookie.Name!;
        return (cookieName, tokens.CookieToken!, tokens.FormFieldName, tokens.RequestToken!);
    }

    private static byte[] Png(int width = 120, int height = 60)
    {
        using var bitmap = new SkiaSharp.SKBitmap(width, height);
        using (var canvas = new SkiaSharp.SKCanvas(bitmap))
        {
            canvas.Clear(SkiaSharp.SKColors.Teal);
        }

        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
