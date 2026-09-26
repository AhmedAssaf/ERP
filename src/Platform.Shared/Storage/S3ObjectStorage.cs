using System.Net;
using Amazon.S3;
using Amazon.S3.Model;

namespace Platform.Shared.Storage;

/// <summary>
/// <see cref="IObjectStorage"/> over the S3 API, path-style (MinIO does not serve virtual-host style). Without settings
/// every call throws <see cref="InvalidOperationException"/>; S3 errors other than a missing key propagate to the caller,
/// which logs the exception type only (an S3 error can echo the endpoint or signature, N-10).
/// </summary>
internal sealed class S3ObjectStorage(ObjectStorageSettings settings) : IObjectStorage, IDisposable
{
    private readonly Lazy<AmazonS3Client> _client = new(() => new AmazonS3Client(
        settings.AccessKey, settings.SecretKey, new AmazonS3Config { ServiceURL = settings.ServiceUrl, ForcePathStyle = true }));

    public async Task PutAsync(string key, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        using var stream = new MemoryStream(content.ToArray(), writable: false);
        await Client.PutObjectAsync(
            new PutObjectRequest { BucketName = settings.BucketName, Key = key, InputStream = stream, ContentType = contentType, AutoCloseStream = false },
            cancellationToken);
    }

    public async Task<StoredObject?> OpenAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        try
        {
            var response = await Client.GetObjectAsync(new GetObjectRequest { BucketName = settings.BucketName, Key = key }, cancellationToken);
            return new StoredObject(response.ResponseStream, response.ContentLength, response.Headers.ContentType ?? "application/octet-stream");
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        // S3 answers a delete of a missing key with success too.
        await Client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = settings.BucketName, Key = key }, cancellationToken);
    }

    public void Dispose()
    {
        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }
    }

    private AmazonS3Client Client => settings.IsConfigured
        ? _client.Value
        : throw new InvalidOperationException($"Object storage is not configured; set {ObjectStorageSettings.Section}:* (see README).");
}
