using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Tenancy.Contracts;
using Platform.Shared.Tenancy;

namespace Platform.Web.Branding;

/// <summary>
/// The logo's two HTTP endpoints (F-02, spec 4.3 and D-10). Uploads never go through the Blazor circuit (ADR-0001): the
/// branding page posts a plain multipart form here. The body limit is enforced before the body is read; the service
/// checks the file itself. Every outcome of a signed-in admin's upload sends the browser back to the page with a
/// <c>logo</c> outcome in the query; a refused caller gets 400 (antiforgery) or 403 (role).
/// </summary>
internal static partial class BrandingEndpoints
{
    public const string UploadPath = "/admin/branding/logo";
    public const string PagePath = "/admin/branding";
    public const string FormField = "logo";

    /// <summary>The largest logo file (spec 4.3).</summary>
    internal const long MaxLogoBytes = 512 * 1024;

    /// <summary>The logo itself plus room for the multipart framing and the antiforgery field.</summary>
    internal const long MaxRequestBytes = MaxLogoBytes + (16 * 1024);

    public static IEndpointRouteBuilder MapBrandingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(UploadPath, UploadAsync).RequireAuthorization(TenantPolicies.TenantAdmin);
        // Anonymous on the tenant's host (TenantMiddleware resolved it, and a platform host 404s /branding), so the logo
        // shows before sign-in too. Only the host tenant's prefix is ever read.
        app.MapGet("/branding/logo/{file}", ServeAsync).AllowAnonymous();
        return app;
    }

    private static async Task<IResult> UploadAsync(
        HttpContext context, IAntiforgery antiforgery, IMemberDirectory members, IBrandingService branding, ILogger<BrandingLog> logger)
    {
        if (context.Request.ContentLength is > MaxRequestBytes)
        {
            return Outcome(context, "too-large");
        }

        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxRequestBytes;
        }

        if (!context.Request.HasFormContentType)
        {
            return Results.BadRequest();
        }

        context.Features.Set<IFormFeature>(new FormFeature(context.Request, new FormOptions
        {
            BufferBodyLengthLimit = MaxRequestBytes,
            MultipartBodyLengthLimit = MaxRequestBytes,
            ValueCountLimit = 8,
        }));

        IFormCollection form;
        try
        {
            form = await context.Request.ReadFormAsync(context.RequestAborted);
        }
        catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException)
        {
            // A body over the limit without a Content-Length, or a malformed multipart body.
            BrandingLog.UploadUnreadable(logger, ex);

            return Outcome(context, "too-large");
        }

        if (!await antiforgery.IsRequestValidAsync(context))
        {
            return Results.BadRequest();
        }

        // The role policy ran on roles cached for up to 30 seconds; the admin role is checked again in the table now.
        var actor = context.User.FindFirst(IdentityClaims.Subject)?.Value;
        if (string.IsNullOrWhiteSpace(actor) || !(await members.GetRolesAsync(actor, context.RequestAborted)).Contains(TenantRoles.TenantAdmin))
        {
            return Results.Forbid();
        }

        if (form.Files.GetFile(FormField) is not { Length: > 0 } file)
        {
            return Outcome(context, "missing");
        }

        if (file.Length > MaxLogoBytes)
        {
            return Outcome(context, "too-large");
        }

        await using var stream = file.OpenReadStream();
        var saved = await branding.SaveLogoAsync(stream, file.ContentType, actor, context.RequestAborted);
        return Outcome(context, saved.IsSuccess ? "saved" : saved.Error.Code switch
        {
            BrandingErrors.LogoTooLarge => "too-large",
            BrandingErrors.LogoNotImage => "not-image",
            BrandingErrors.LogoTooManyPixels => "too-many-pixels",
            BrandingErrors.NotAllowed => "not-allowed",
            _ => "unreadable",
        });
    }

    private static async Task<IResult> ServeAsync(string file, HttpContext context, ITenantAccessor tenants, IBrandingService branding)
    {
        var match = LogoFile().Match(file);
        if (tenants.Current is null || !match.Success)
        {
            return Results.NotFound();
        }

        var logo = await branding.OpenLogoAsync(match.Groups["hash"].Value, context.RequestAborted);
        if (logo is null)
        {
            return Results.NotFound();
        }

        // The name is the content's hash, so the response never changes.
        context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        context.Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
        return Results.Stream(logo, "image/png");
    }

    /// <summary>Back to the page with the outcome, as a GET (303), so a reload does not post the file again.</summary>
    private static IResult Outcome(HttpContext context, string outcome)
    {
        context.Response.Headers.Location = $"{PagePath}?logo={outcome}";
        return Results.StatusCode(StatusCodes.Status303SeeOther);
    }

    [GeneratedRegex(@"^(?<hash>[a-f0-9]{64})\.png\z", RegexOptions.CultureInvariant)]
    private static partial Regex LogoFile();
}

/// <summary>Log messages of the branding endpoints, and their log category.</summary>
internal sealed partial class BrandingLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "A logo upload could not be read as a form within the size limit.")]
    public static partial void UploadUnreadable(ILogger logger, Exception exception);
}
