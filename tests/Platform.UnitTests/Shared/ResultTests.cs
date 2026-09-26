using Platform.Shared.Results;

namespace Platform.UnitTests.Shared;

public class ResultTests
{
    [Fact]
    public void Success_carries_the_value_and_no_error()
    {
        var result = Result.Success(42);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(42);
        result.Error.ShouldBeNull();
    }

    [Fact]
    public void Failure_carries_the_error_and_reading_the_value_throws()
    {
        var result = Result.Failure<int>(Error.NotFound("tender.not_found", "The tender was not found."));

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
        result.Error.Code.ShouldBe("tender.not_found");
        Should.Throw<InvalidOperationException>(() => result.Value);
    }
}
