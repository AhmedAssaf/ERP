using Microsoft.Extensions.Configuration;

namespace Platform.Modules.Operations.PlatformConsole;

/// <summary>
/// Object storage as the web host reads it (section <c>ObjectStorage</c>): path-style S3, MinIO in Development. The access
/// key and secret come from user secrets or the environment only (N-10). Any setting missing means "not configured".
/// </summary>
internal sealed record ObjectStorageSettings(string? ServiceUrl, string? BucketName, string? AccessKey, string? SecretKey)
{
    public const string Section = "ObjectStorage";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ServiceUrl) && !string.IsNullOrWhiteSpace(BucketName)
        && !string.IsNullOrWhiteSpace(AccessKey) && !string.IsNullOrWhiteSpace(SecretKey);

    public static ObjectStorageSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(Section);
        return new ObjectStorageSettings(section["ServiceUrl"], section["BucketName"], section["AccessKey"], section["SecretKey"]);
    }
}
