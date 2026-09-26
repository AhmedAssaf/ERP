using Microsoft.Extensions.Configuration;

namespace Platform.Shared.Storage;

/// <summary>
/// Object storage settings (section <c>ObjectStorage</c>): path-style S3, MinIO in Development. One section for every
/// user of the bucket: tenant storage usage in the console (F-54) and tenant logos (F-02). The access key and secret come
/// from user secrets or the environment only (N-10). Any setting missing means "not configured".
/// </summary>
public sealed record ObjectStorageSettings(string? ServiceUrl, string? BucketName, string? AccessKey, string? SecretKey)
{
    public const string Section = "ObjectStorage";

    /// <summary>No settings: storage is not configured.</summary>
    public static ObjectStorageSettings None { get; } = new(null, null, null, null);

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ServiceUrl) && !string.IsNullOrWhiteSpace(BucketName)
        && !string.IsNullOrWhiteSpace(AccessKey) && !string.IsNullOrWhiteSpace(SecretKey);

    public static ObjectStorageSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(Section);
        return new ObjectStorageSettings(section["ServiceUrl"], section["BucketName"], section["AccessKey"], section["SecretKey"]);
    }

    // The secret never appears in logs or exception text (N-10), even if the record is printed.
    public override string ToString() =>
        $"ObjectStorageSettings {{ ServiceUrl = {ServiceUrl}, BucketName = {BucketName}, Configured = {IsConfigured} }}";
}
