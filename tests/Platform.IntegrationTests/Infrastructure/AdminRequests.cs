using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Platform.Modules.Identity.Contracts;
using Platform.Shared.Tenancy;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>
/// Requests against the tenant administration surface (<c>/admin/staff</c>, <c>/admin/branding</c>,
/// <c>POST /admin/branding/logo</c>, <c>/branding/logo/{hash}.png</c>) as a browser sends them: the header-driven test
/// sign-in, and for the logo form the multipart body with a valid antiforgery field and cookie for the signed-in user.
/// Shared by the QA isolation, role-matrix and localisation tests.
/// </summary>
internal static class AdminRequests
{
    public const string StaffPath = "/admin/staff";
    public const string BrandingPath = "/admin/branding";
    public const string LogoPath = "/admin/branding/logo";

    /// <summary>The two admin pages, for theories.</summary>
    public static TheoryData<string> Pages => new(StaffPath, BrandingPath);

    /// <summary>The web host with test sign-in and object storage on the given MinIO.</summary>
    public static WebApplicationFactory<Program> Factory(string appConnectionString, MinioFixture minio) =>
        new PlatformWebFactory(appConnectionString).WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in minio.Settings)
            {
                builder.UseSetting(key, value);
            }
        });

    public static HttpClient Client(WebApplicationFactory<Program> factory, string host) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri($"http://{host}"),
            AllowAutoRedirect = false,
            HandleCookies = false,
        });

    /// <summary>A throwaway tenant (its own host and organization alias) with one active member holding <paramref name="roles"/>.</summary>
    public static async Task<(TenantContext Tenant, TestUser User)> TenantWithMemberAsync(
        DatabaseFixture db, string[] roles, string? locale, CancellationToken cancellationToken)
    {
        var tenant = await TenantRows.InsertAsync(db.OwnerConnectionString, cancellationToken);
        var user = await MemberAsync(db, tenant, roles, locale, cancellationToken);
        return (tenant, user);
    }

    /// <summary>A new active member of <paramref name="tenant"/> with <paramref name="roles"/> (none: no member row at all).</summary>
    public static async Task<TestUser> MemberAsync(
        DatabaseFixture db, TenantContext tenant, string[] roles, string? locale, CancellationToken cancellationToken)
    {
        var user = new TestUser($"qa-{Guid.NewGuid():N}", [tenant.KeycloakOrgAlias], locale);
        if (roles.Length > 0)
        {
            await MemberRows.InsertAsync(
                db.AppConnectionString, tenant.TenantId, user.Subject, $"{user.Subject}@{tenant.Slug}.example.sa", roles, "active", cancellationToken);
        }

        return user;
    }

    public static TestUser Admin(TenantContext tenant) => new($"qa-admin-{Guid.NewGuid():N}", [tenant.KeycloakOrgAlias], "en");

    public static Task<HttpResponseMessage> GetAsync(HttpClient client, string path, TestUser? user, CancellationToken cancellationToken, string? cookie = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (user is not null)
        {
            request.As(user);
        }

        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        return client.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Posts the logo form as the branding page does. With <paramref name="user"/> null the request is anonymous but
    /// still carries a well-formed antiforgery pair (issued for an anonymous user), so only authentication can refuse it.
    /// </summary>
    public static async Task<HttpResponseMessage> PostLogoAsync(
        WebApplicationFactory<Program> factory,
        HttpClient client,
        TestUser? user,
        byte[] content,
        string contentType,
        CancellationToken cancellationToken,
        string fileName = "logo.png")
    {
        var tokens = AntiforgeryTokens(factory.Services, user);
        using var form = new MultipartFormDataContent { { new StringContent(tokens.RequestToken), tokens.FormField } };
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "logo", fileName);

        using var request = new HttpRequestMessage(HttpMethod.Post, LogoPath) { Content = form };
        if (user is not null)
        {
            request.As(user);
        }

        request.Headers.Add("Cookie", $"{tokens.CookieName}={tokens.CookieToken}");
        return await client.SendAsync(request, cancellationToken);
    }

    public static (string CookieName, string CookieToken, string FormField, string RequestToken) AntiforgeryTokens(
        IServiceProvider services, TestUser? user)
    {
        using var scope = services.CreateScope();
        var identity = user is null
            ? new ClaimsIdentity()
            : new ClaimsIdentity([new Claim("sub", user.Subject)], TestAuthHandler.SchemeName);
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider, User = new ClaimsPrincipal(identity) };
        var tokens = scope.ServiceProvider.GetRequiredService<IAntiforgery>().GetTokens(context);
        var cookieName = services.GetRequiredService<IOptions<AntiforgeryOptions>>().Value.Cookie.Name!;
        return (cookieName, tokens.CookieToken!, tokens.FormFieldName, tokens.RequestToken!);
    }

    /// <summary>A small solid PNG, optionally followed by bytes a browser might sniff as markup.</summary>
    public static byte[] Png(int width = 120, int height = 60, byte[]? trailer = null)
    {
        using var bitmap = new SkiaSharp.SKBitmap(width, height);
        using (var canvas = new SkiaSharp.SKCanvas(bitmap))
        {
            canvas.Clear(SkiaSharp.SKColors.Teal);
        }

        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return trailer is null ? data.ToArray() : [.. data.ToArray(), .. trailer];
    }

    /// <summary>Every tenant role, for theories over the role matrix.</summary>
    public static IReadOnlyList<string> Roles => TenantRoles.All;
}
