using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using Platform.Modules.Vendors;
using Platform.Modules.Vendors.Contracts;
using Platform.Shared.Results;
using Platform.Shared.Tenancy;

namespace Platform.Web.Vendor;

/// <summary>
/// The vendor document upload API (F-12, V-9, ADR-0001): files never go through the Blazor circuit; the FileUpload
/// component's script calls these endpoints with <c>fetch</c>.
/// <list type="bullet">
/// <item><c>POST /vendor/uploads</c> <c>{documentType, fileName, size, contentType}</c> → 201 <c>{uploadId, chunkSize, chunkCount}</c>.</item>
/// <item><c>PUT /vendor/uploads/{uploadId}/chunks/{index}</c>, the raw chunk (at most 1 MB, <c>Content-Length</c>
/// required) with its SHA-256 in hex in <c>X-Chunk-Sha256</c> → 204. Sending an index again replaces it.</item>
/// <item><c>POST /vendor/uploads/{uploadId}/complete</c> <c>{expiresOn}</c> → 200 <c>{documentId, sha256, status: "clean"}</c>,
/// or 202 with <c>status: "pending_scan"</c> while the scanner is unavailable.</item>
/// </list>
/// Every request needs the Vendor policy and the antiforgery token in the <c>RequestVerificationToken</c> header (with its
/// cookie); a refused one gets 400 (antiforgery), 401 or 403. Size limits are checked from <c>Content-Length</c> and set
/// on the request before any body is read. Expected failures answer <c>{code}</c> (see <see cref="VendorDocumentErrors"/>):
/// 400 input, 404 an upload that is not the caller's company's (or older than a day), 409 state, 422 infected, 429
/// <c>vendor.too_many_uploads</c> while the company has 10 open uploads or has started <c>Vendors:MaxUploadsPerDay</c>
/// (30) in the last 24 hours. Requests are limited per vendor company to
/// <c>Vendors:UploadRequestsPerMinute</c> (120) in a fixed one-minute window; over it, 429 without a body.
/// </summary>
internal static class UploadEndpoints
{
    public const string BasePath = "/vendor/uploads";
    public const string ChunkHashHeader = "X-Chunk-Sha256";

    /// <summary>The rate limiter policy of the upload API: a fixed window per vendor company.</summary>
    public const string RateLimitPolicy = "vendor-uploads";

    /// <summary>The JSON bodies are small; anything larger is not a start or complete request.</summary>
    internal const long MaxJsonBytes = 4 * 1024;

    /// <summary>
    /// The upload API's rate limit (V-9): per vendor company, <c>Vendors:UploadRequestsPerMinute</c> requests in a fixed
    /// one-minute window, no queue. The company is the vendor context the Vendor policy set, so the limiter must run
    /// after the vendor context middleware; a request without one is refused by the policy before it gets here.
    /// </summary>
    public static IServiceCollection AddVendorUploadRateLimit(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(RateLimitPolicy, context =>
            {
                if (context.RequestServices.GetRequiredService<IVendorAccessor>().Current?.CompanyId is not { } companyId)
                {
                    return RateLimitPartition.GetNoLimiter(Guid.Empty);
                }

                var permits = VendorsModule.UploadRequestsPerMinute(context.RequestServices);
                return RateLimitPartition.GetFixedWindowLimiter(companyId, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permits,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                });
            });
        });
        return services;
    }

    public static IEndpointRouteBuilder MapVendorUploadEndpoints(this IEndpointRouteBuilder app)
    {
        var uploads = app.MapGroup(BasePath).RequireAuthorization(VendorPolicies.Vendor).RequireRateLimiting(RateLimitPolicy);
        uploads.MapPost(string.Empty, StartAsync);
        uploads.MapPut("{uploadId:guid}/chunks/{index:int}", PutChunkAsync);
        uploads.MapPost("{uploadId:guid}/complete", CompleteAsync);
        return app;
    }

    private static async Task<IResult> StartAsync(HttpContext context, IAntiforgery antiforgery, IVendorAccessor vendors, IVendorUploads uploads)
    {
        if (await RefuseAsync(context, antiforgery, vendors, MaxJsonBytes) is { } refused)
        {
            return refused;
        }

        var (request, invalid) = await ReadJsonAsync<StartRequest>(context);
        if (invalid is not null)
        {
            return invalid;
        }

        var started = await uploads.StartAsync(
            new VendorUploadStart(request!.DocumentType, request.FileName, request.Size, request.ContentType), context.RequestAborted);
        return started.IsSuccess
            ? Results.Json(new StartResponse(started.Value.UploadId, started.Value.ChunkSize, started.Value.ChunkCount), statusCode: StatusCodes.Status201Created)
            : Failure(started.Error);
    }

    private static async Task<IResult> PutChunkAsync(
        Guid uploadId, int index, HttpContext context, IAntiforgery antiforgery, IVendorAccessor vendors, IVendorUploads uploads)
    {
        if (context.Request.ContentLength is not { } length)
        {
            return Results.StatusCode(StatusCodes.Status411LengthRequired);
        }

        if (await RefuseAsync(context, antiforgery, vendors, VendorDocumentLimits.ChunkBytes) is { } refused)
        {
            return refused;
        }

        var chunk = new byte[length];
        try
        {
            await context.Request.Body.ReadExactlyAsync(chunk, context.RequestAborted);
        }
        catch (Exception ex) when (ex is EndOfStreamException or BadHttpRequestException or IOException)
        {
            // The connection dropped mid-chunk: the browser sends this chunk again.
            return Failure(Error.Validation(VendorDocumentErrors.ChunkWrongSize, "The chunk did not arrive whole."));
        }

        var stored = await uploads.PutChunkAsync(uploadId, index, chunk, context.Request.Headers[ChunkHashHeader].ToString(), context.RequestAborted);
        return stored.IsSuccess ? Results.NoContent() : Failure(stored.Error);
    }

    private static async Task<IResult> CompleteAsync(
        Guid uploadId, HttpContext context, IAntiforgery antiforgery, IVendorAccessor vendors, IVendorUploads uploads)
    {
        if (await RefuseAsync(context, antiforgery, vendors, MaxJsonBytes) is { } refused)
        {
            return refused;
        }

        var (request, invalid) = await ReadJsonAsync<CompleteRequest>(context);
        if (invalid is not null)
        {
            return invalid;
        }

        if (request!.ExpiresOn is not { } expiresOn)
        {
            return Failure(Error.Validation(VendorDocumentErrors.InvalidExpiry, "Enter the document's expiry date."));
        }

        var completed = await uploads.CompleteAsync(uploadId, expiresOn, context.RequestAborted);
        if (!completed.IsSuccess)
        {
            return Failure(completed.Error);
        }

        var clean = completed.Value.Status == VendorDocumentStatus.Clean;
        return Results.Json(
            new CompleteResponse(completed.Value.DocumentId, completed.Value.Sha256, clean ? "clean" : "pending_scan"),
            statusCode: clean ? StatusCodes.Status200OK : StatusCodes.Status202Accepted);
    }

    /// <summary>
    /// The checks before any body is read: the declared length within <paramref name="maxBytes"/> (and the same limit set
    /// on the request for a body that lies about it), the antiforgery token, and the vendor context the Vendor policy set.
    /// </summary>
    private static async Task<IResult?> RefuseAsync(HttpContext context, IAntiforgery antiforgery, IVendorAccessor vendors, long maxBytes)
    {
        if (context.Request.ContentLength is > 0 and var length && length > maxBytes)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = maxBytes;
        }

        if (!await antiforgery.IsRequestValidAsync(context))
        {
            return Results.BadRequest();
        }

        return vendors.Current is null ? Results.Forbid() : null;
    }

    private static async Task<(T? Value, IResult? Invalid)> ReadJsonAsync<T>(HttpContext context)
        where T : class
    {
        if (!context.Request.HasJsonContentType())
        {
            return (null, Results.StatusCode(StatusCodes.Status415UnsupportedMediaType));
        }

        try
        {
            var value = await context.Request.ReadFromJsonAsync<T>(context.RequestAborted);
            return value is null ? (null, Results.BadRequest()) : (value, null);
        }
        catch (JsonException)
        {
            return (null, Results.BadRequest());
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return (null, Results.StatusCode(StatusCodes.Status413PayloadTooLarge));
        }
    }

    private static IResult Failure(Error error) => Results.Json(
        new FailureResponse(error.Code),
        statusCode: error.Code == VendorDocumentErrors.TooManyUploads ? StatusCodes.Status429TooManyRequests : error.Kind switch
        {
            ErrorKind.NotFound => StatusCodes.Status404NotFound,
            ErrorKind.Conflict => StatusCodes.Status409Conflict,
            ErrorKind.Refused => StatusCodes.Status422UnprocessableEntity,
            _ => StatusCodes.Status400BadRequest,
        });

    private sealed record StartRequest(string? DocumentType, string? FileName, long Size, string? ContentType);

    private sealed record StartResponse(Guid UploadId, int ChunkSize, int ChunkCount);

    private sealed record CompleteRequest(DateOnly? ExpiresOn);

    private sealed record CompleteResponse(Guid DocumentId, string Sha256, string Status);

    /// <summary>Only the stable code: the page shows the localized text for it.</summary>
    private sealed record FailureResponse(string Code);
}
