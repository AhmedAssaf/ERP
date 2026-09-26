namespace Platform.Shared.Storage;

/// <summary>An object read from storage. Dispose it to release the connection.</summary>
public sealed class StoredObject(Stream content, long length, string contentType) : IAsyncDisposable, IDisposable
{
    public Stream Content { get; } = content;

    public long Length { get; } = length;

    public string ContentType { get; } = contentType;

    public ValueTask DisposeAsync() => Content.DisposeAsync();

    public void Dispose() => Content.Dispose();
}

/// <summary>
/// The platform's object storage bucket (S3 API; MinIO locally), keyed by path. Tenant data lives under
/// <c>tenants/{tenantId}/</c>; the caller builds the key and is responsible for putting the tenant id in it.
/// </summary>
public interface IObjectStorage
{
    /// <summary>Stores the bytes under the key, replacing any object there.</summary>
    Task PutAsync(string key, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the stream from its current position to its end under the key, replacing any object there. The stream must
    /// be seekable (its length is sent first); it is read, not disposed.
    /// </summary>
    Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default);

    /// <summary>The object under the key, or null when there is none.</summary>
    Task<StoredObject?> OpenAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Deletes the object under the key; a key with no object is not an error.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}
