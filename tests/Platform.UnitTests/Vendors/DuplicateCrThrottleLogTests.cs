using Platform.Modules.Vendors.Registration;
using StackExchange.Redis;

namespace Platform.UnitTests.Vendors;

/// <summary>
/// W-34 (M-1): the outage Warning names the Redis server's error code, the first upper-case word of its reply, and
/// nothing else of the message, which can name a user, a key or a script (N-10). And a refund gives a place back in
/// memory without going below zero.
/// </summary>
public sealed class DuplicateCrThrottleLogTests
{
    [Theory]
    [InlineData("OOM command not allowed when used memory > 'maxmemory'. script: 4a1f, on @user_script:9.", "OOM")]
    [InlineData("READONLY You can't write against a read only replica.", "READONLY")]
    [InlineData("NOPERM User throttle-user has no permissions to run the 'eval' command", "NOPERM")]
    [InlineData("ERR Error running script", "ERR")]
    [InlineData("no code here", null)]
    [InlineData("A", null)]
    public void The_server_error_code_is_the_first_upper_case_word_only(string message, string? code) =>
        DuplicateCrThrottle.ServerErrorCode(new RedisServerException(default(RedisErrorKind), CommandFlags.None, message)).ShouldBe(code);

    [Fact]
    public void An_exception_that_is_not_a_server_reply_has_no_code() =>
        DuplicateCrThrottle.ServerErrorCode(new RedisConnectionException(ConnectionFailureType.UnableToConnect, CommandFlags.None, "ERR not a server reply", null, CommandStatus.Unknown)).ShouldBeNull();

    [Fact]
    public void A_release_gives_a_record_back_but_never_goes_below_zero()
    {
        var counter = new InProcessWindowCounter(TimeProvider.System, limit: 2, TimeSpan.FromHours(1), capacity: 10);
        counter.Record("k");
        counter.Record("k");
        counter.IsLimited("k").ShouldBeTrue();

        counter.Release("k");
        counter.IsLimited("k").ShouldBeFalse();
        counter.Release("k");
        counter.Release("k");
        counter.Record("k");
        counter.IsLimited("k").ShouldBeFalse("one record after releases that stopped at zero");
        counter.Release("unknown");
    }
}
