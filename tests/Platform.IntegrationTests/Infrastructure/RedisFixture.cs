using StackExchange.Redis;
using Testcontainers.Redis;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>
/// W-34: Redis 7, the image of the Compose service <c>erp-redis</c>, for the duplicate-CR throttle and the Redis health
/// check. No password, no persistence, as in the local stack. Tests share it, so each one uses keys of its own (a fresh
/// user id or address) and never flushes the database.
/// </summary>
public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder("redis:7-alpine").Build();

    /// <summary>The <c>ConnectionStrings:Redis</c> value for the container, as a host would read it.</summary>
    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    /// <summary>A separate connection for assertions (TTLs, keys), not the one under test.</summary>
    public Task<ConnectionMultiplexer> ConnectAsync() => ConnectionMultiplexer.ConnectAsync(ConnectionString);
}

[CollectionDefinition(Name)]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>
{
    public const string Name = "redis";
}
