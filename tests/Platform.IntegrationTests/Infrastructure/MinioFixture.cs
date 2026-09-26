using Amazon.S3;
using Amazon.S3.Model;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>MinIO (generic container, plan task 3), path-style S3 the same way the health check and D-9 branding use it.</summary>
public sealed class MinioFixture : IAsyncLifetime
{
    public const string BucketName = "erp-dev-test";
    private const string RootUser = "erp-test";
    private const string RootPassword = "erp_test_password";

    private readonly IContainer _container = new ContainerBuilder("quay.io/minio/minio:latest")
        .WithCommand("server", "/data")
        .WithPortBinding(9000, assignRandomHostPort: true)
        .WithEnvironment("MINIO_ROOT_USER", RootUser)
        .WithEnvironment("MINIO_ROOT_PASSWORD", RootPassword)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(9000))
        .Build();

    public string ServiceUrl => $"http://{_container.Hostname}:{_container.GetMappedPublicPort(9000)}";

    public static string AccessKey => RootUser;

    public static string SecretKey => RootPassword;

    /// <summary>The web host's <c>ObjectStorage:*</c> settings for this container.</summary>
    public IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["ObjectStorage:ServiceUrl"] = ServiceUrl,
        ["ObjectStorage:BucketName"] = BucketName,
        ["ObjectStorage:AccessKey"] = AccessKey,
        ["ObjectStorage:SecretKey"] = SecretKey,
    };

    /// <summary>The object's bytes, or null when the bucket has no such key.</summary>
    public async Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        using var client = new AmazonS3Client(AccessKey, SecretKey, new AmazonS3Config { ServiceURL = ServiceUrl, ForcePathStyle = true });
        try
        {
            using var response = await client.GetObjectAsync(BucketName, key, cancellationToken);
            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, cancellationToken);
            return buffer.ToArray();
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await EnsureBucketAsync();
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    // The port answers before the server is fully ready to serve S3 requests; retry until it accepts one.
    private async Task EnsureBucketAsync()
    {
        var config = new AmazonS3Config { ServiceURL = ServiceUrl, ForcePathStyle = true };
        using var client = new AmazonS3Client(AccessKey, SecretKey, config);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try
            {
                await client.PutBucketAsync(new PutBucketRequest { BucketName = BucketName });
                return;
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(500);
            }
        }
    }
}

[CollectionDefinition(Name)]
public sealed class MinioCollection : ICollectionFixture<MinioFixture>
{
    public const string Name = "minio";
}
