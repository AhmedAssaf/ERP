// Spike 2 (docs/05 section 5): does a Blazor Server upload wizard survive a 50 MB file on a throttled
// 3G profile with a multi-second connectivity gap? Pass: upload completes, circuit reconnects, draft state survives.

using BlazorUploadSpike.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(o =>
    {
        // How long a disconnected circuit is kept server-side so the browser can reconnect to it.
        o.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(3);
        o.DetailedErrors = true;
    })
    .AddHubOptions(o =>
    {
        // InputFile streams the file through the circuit in chunks; the default 32 KB cap makes 50 MB very chatty.
        o.MaximumReceiveMessageSize = 1024 * 1024;
        o.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
        o.KeepAliveInterval = TimeSpan.FromSeconds(10);
    });

var app = builder.Build();

app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Direct chunked upload endpoints (fallback path that does not depend on the circuit).
var uploadRoot = Path.Combine(Path.GetTempPath(), "upload-spike-chunks");
Directory.CreateDirectory(uploadRoot);

app.MapPut("/api/upload/{id:guid}/{index:int}", async (Guid id, int index, HttpRequest req) =>
{
    var path = Path.Combine(uploadRoot, $"{id:N}.{index:D6}");
    await using var f = File.Create(path);           // idempotent: a retried chunk overwrites itself
    await req.Body.CopyToAsync(f);
    return Results.Ok(new { index, bytes = f.Length });
}).DisableAntiforgery();

app.MapPost("/api/upload/{id:guid}/complete", async (Guid id) =>
{
    var parts = Directory.GetFiles(uploadRoot, $"{id:N}.*").OrderBy(x => x).ToArray();
    using var sha = System.Security.Cryptography.SHA256.Create();
    long size = 0;
    foreach (var part in parts)
    {
        var bytes = await File.ReadAllBytesAsync(part);
        sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        size += bytes.Length;
        File.Delete(part);
    }
    sha.TransformFinalBlock([], 0, 0);
    return Results.Ok(new { size, chunks = parts.Length, sha256 = Convert.ToHexString(sha.Hash!)[..16] });
}).DisableAntiforgery();

app.Run("http://127.0.0.1:5273");
