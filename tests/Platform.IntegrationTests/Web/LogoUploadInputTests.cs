using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;

namespace Platform.IntegrationTests.Web;

/// <summary>
/// QA pass, F-02 and D-10 negative inputs on <c>POST /admin/branding/logo</c> beyond the existing type and size cases: a
/// body over the limit sent without a Content-Length, a body that is not a form, an image declared with a wrong type,
/// and a real image carrying markup after its end, which the re-encoding must drop.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class LogoUploadInputTests(DatabaseFixture db, MinioFixture minio) : IClassFixture<MinioFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_oversized_upload_without_a_content_length_is_refused_as_too_large()
    {
        var (tenant, admin) = await AdminRequests.TenantWithMemberAsync(db, [TenantRoles.TenantAdmin], "en", Ct);
        await using var factory = AdminRequests.Factory(db.AppConnectionString, minio);
        using var client = AdminRequests.Client(factory, TenantRows.Host(tenant));
        var tokens = AdminRequests.AntiforgeryTokens(factory.Services, admin);
        using var form = new MultipartFormDataContent { { new StringContent(tokens.RequestToken), tokens.FormField } };
        var file = new ByteArrayContent(AdminRequests.Png(trailer: new byte[700 * 1024]));
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "logo", "logo.png");
        using var request = new HttpRequestMessage(HttpMethod.Post, AdminRequests.LogoPath) { Content = new NoLengthContent(form) }.As(admin);
        request.Headers.Add("Cookie", $"{tokens.CookieName}={tokens.CookieToken}");

        using var response = await client.SendAsync(request, Ct);

        request.Content!.Headers.ContentLength.ShouldBeNull();
        response.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        response.Headers.Location!.OriginalString.ShouldBe("/admin/branding?logo=too-large");
        (await TenantRows.BrandingAsync(db.AppConnectionString, tenant, Ct)).LogoUrl.ShouldBeNull();
    }

    [Fact]
    public async Task A_body_that_is_not_a_form_is_a_bad_request()
    {
        var (tenant, admin) = await AdminRequests.TenantWithMemberAsync(db, [TenantRoles.TenantAdmin], "en", Ct);
        await using var factory = AdminRequests.Factory(db.AppConnectionString, minio);
        using var client = AdminRequests.Client(factory, TenantRows.Host(tenant));
        var body = new ByteArrayContent(AdminRequests.Png());
        body.Headers.ContentType = new MediaTypeHeaderValue("image/png");

        using var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, AdminRequests.LogoPath) { Content = body }.As(admin), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await TenantRows.BrandingAsync(db.AppConnectionString, tenant, Ct)).LogoUrl.ShouldBeNull();
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("image/svg+xml")]
    [InlineData("application/octet-stream")]
    public async Task A_png_declared_with_a_type_other_than_png_or_jpeg_is_refused(string contentType)
    {
        var (tenant, admin) = await AdminRequests.TenantWithMemberAsync(db, [TenantRoles.TenantAdmin], "en", Ct);
        await using var factory = AdminRequests.Factory(db.AppConnectionString, minio);
        using var client = AdminRequests.Client(factory, TenantRows.Host(tenant));

        using var response = await AdminRequests.PostLogoAsync(factory, client, admin, AdminRequests.Png(), contentType, Ct);

        response.Headers.Location!.OriginalString.ShouldBe("/admin/branding?logo=not-image");
        (await TenantRows.BrandingAsync(db.AppConnectionString, tenant, Ct)).LogoUrl.ShouldBeNull();
    }

    [Fact]
    public async Task Markup_appended_to_a_real_png_is_not_in_the_served_logo()
    {
        var (tenant, admin) = await AdminRequests.TenantWithMemberAsync(db, [TenantRoles.TenantAdmin], "en", Ct);
        await using var factory = AdminRequests.Factory(db.AppConnectionString, minio);
        using var client = AdminRequests.Client(factory, TenantRows.Host(tenant));
        var trailer = Encoding.ASCII.GetBytes("<html><script>alert(document.cookie)</script></html>");

        using (var upload = await AdminRequests.PostLogoAsync(factory, client, admin, AdminRequests.Png(trailer: trailer), "image/png", Ct))
        {
            upload.Headers.Location!.OriginalString.ShouldBe("/admin/branding?logo=saved");
        }

        var logoUrl = (await TenantRows.BrandingAsync(db.AppConnectionString, tenant, Ct)).LogoUrl.ShouldNotBeNull();
        using var served = await client.GetAsync(new Uri(logoUrl, UriKind.Relative), Ct);
        var bytes = await served.Content.ReadAsByteArrayAsync(Ct);
        Encoding.ASCII.GetString(bytes).ShouldNotContain("<script", Case.Insensitive);
        served.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
    }

    [Fact]
    public async Task A_second_file_field_does_not_replace_the_logo_field()
    {
        var (tenant, admin) = await AdminRequests.TenantWithMemberAsync(db, [TenantRoles.TenantAdmin], "en", Ct);
        await using var factory = AdminRequests.Factory(db.AppConnectionString, minio);
        using var client = AdminRequests.Client(factory, TenantRows.Host(tenant));
        var tokens = AdminRequests.AntiforgeryTokens(factory.Services, admin);
        using var form = new MultipartFormDataContent { { new StringContent(tokens.RequestToken), tokens.FormField } };
        var svg = new ByteArrayContent("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8.ToArray());
        svg.Headers.ContentType = new MediaTypeHeaderValue("image/svg+xml");
        form.Add(svg, "other", "logo.svg");
        using var request = new HttpRequestMessage(HttpMethod.Post, AdminRequests.LogoPath) { Content = form }.As(admin);
        request.Headers.Add("Cookie", $"{tokens.CookieName}={tokens.CookieToken}");

        using var response = await client.SendAsync(request, Ct);

        response.Headers.Location!.OriginalString.ShouldBe("/admin/branding?logo=missing");
        (await TenantRows.BrandingAsync(db.AppConnectionString, tenant, Ct)).LogoUrl.ShouldBeNull();
    }

    /// <summary>Sends the inner content without a Content-Length, as a chunked browser or script upload would.</summary>
    private sealed class NoLengthContent : HttpContent
    {
        private readonly HttpContent _inner;

        public NoLengthContent(HttpContent inner)
        {
            _inner = inner;
            foreach (var header in inner.Headers.Where(h => !h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)))
            {
                Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => _inner.CopyToAsync(stream);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
