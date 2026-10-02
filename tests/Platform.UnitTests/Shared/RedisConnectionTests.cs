using Platform.Shared.Caching;
using StackExchange.Redis;

namespace Platform.UnitTests.Shared;

/// <summary>
/// W-34: the host's Redis options. The host starts while Redis is down (no abort on connect), a call made while
/// disconnected fails at once (fail-fast backlog), and a Redis that stops answering costs a call about a second instead of
/// the library's five, unless the connection string sets its own timeouts.
/// </summary>
public sealed class RedisConnectionTests
{
    [Fact]
    public void Without_timeouts_in_the_connection_string_the_lower_ones_apply()
    {
        var options = RedisConnection.Options("localhost:6379");

        options.AbortOnConnectFail.ShouldBeFalse();
        options.BacklogPolicy.ShouldBe(BacklogPolicy.FailFast);
        options.AsyncTimeout.ShouldBe(RedisConnection.OperationTimeoutMilliseconds);
        options.SyncTimeout.ShouldBe(RedisConnection.OperationTimeoutMilliseconds);
        options.ConnectTimeout.ShouldBe(RedisConnection.ConnectTimeoutMilliseconds);
        options.ConnectRetry.ShouldBe(1);
        RedisConnection.OperationTimeoutMilliseconds.ShouldBe(1000);
    }

    [Fact]
    public void Timeouts_the_connection_string_sets_are_kept()
    {
        var options = RedisConnection.Options("redis.internal:6380, AsyncTimeout=4000 ,syncTimeout=3000,connectTimeout=7000,connectRetry=4,password=p,abortConnect=true");

        options.AsyncTimeout.ShouldBe(4000);
        options.SyncTimeout.ShouldBe(3000);
        options.ConnectTimeout.ShouldBe(7000);
        options.ConnectRetry.ShouldBe(4);
        options.AbortOnConnectFail.ShouldBeFalse("the host must start while Redis is down, whatever the connection string says");
    }

    [Fact]
    public void Setting_one_timeout_leaves_the_others_lowered()
    {
        var options = RedisConnection.Options("localhost:6379,asyncTimeout=2500");

        options.AsyncTimeout.ShouldBe(2500);
        options.SyncTimeout.ShouldBe(RedisConnection.OperationTimeoutMilliseconds);
        options.ConnectTimeout.ShouldBe(RedisConnection.ConnectTimeoutMilliseconds);
    }
}
