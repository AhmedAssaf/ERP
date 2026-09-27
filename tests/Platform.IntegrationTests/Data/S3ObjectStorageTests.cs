using Amazon.S3;
using Platform.IntegrationTests.Infrastructure;
using Platform.Shared.Storage;

namespace Platform.IntegrationTests.Data;

/// <summary>
/// Object storage over MinIO: only a missing key reads as "no such object". A missing bucket is a misconfiguration and
/// must surface as a failure, so the vendor retry scan treats it as an outage (never charged) instead of parking every
/// document as if its file were gone (V-10).
/// </summary>
public sealed class S3ObjectStorageTests(MinioFixture minio) : IClassFixture<MinioFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_missing_key_reads_as_null()
    {
        using var storage = Storage(MinioFixture.BucketName);

        (await storage.OpenAsync($"missing/{Guid.NewGuid()}", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task A_missing_bucket_throws_instead_of_reading_as_a_missing_key()
    {
        using var storage = Storage($"no-such-bucket-{Guid.NewGuid():N}"[..40]);

        var thrown = await Should.ThrowAsync<AmazonS3Exception>(() => storage.OpenAsync($"missing/{Guid.NewGuid()}", Ct));

        thrown.ErrorCode.ShouldBe("NoSuchBucket");
    }

    private S3ObjectStorage Storage(string bucket) =>
        new(new ObjectStorageSettings(minio.ServiceUrl, bucket, MinioFixture.AccessKey, MinioFixture.SecretKey));
}
