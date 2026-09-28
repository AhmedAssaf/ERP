using Platform.Shared.Tenancy;

namespace Platform.UnitTests.Shared;

public class ActingUserAccessorTests
{
    [Fact]
    public void Set_stores_the_user()
    {
        var accessor = new ActingUserAccessor();

        accessor.Set("user-1");

        accessor.UserId.ShouldBe("user-1");
    }

    [Fact]
    public void Nothing_is_set_until_the_host_sets_it()
    {
        new ActingUserAccessor().UserId.ShouldBeNull();
    }

    [Fact]
    public void Setting_the_same_user_again_is_a_no_op()
    {
        var accessor = new ActingUserAccessor();
        accessor.Set("user-1");

        accessor.Set("user-1");

        accessor.UserId.ShouldBe("user-1");
    }

    [Fact]
    public void Setting_a_different_user_throws()
    {
        var accessor = new ActingUserAccessor();
        accessor.Set("user-1");

        Should.Throw<InvalidOperationException>(() => accessor.Set("user-2"));
        accessor.UserId.ShouldBe("user-1");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_user_id_is_refused(string userId)
    {
        var accessor = new ActingUserAccessor();

        Should.Throw<ArgumentException>(() => accessor.Set(userId));
    }

    [Fact]
    public void Setting_null_throws()
    {
        Should.Throw<ArgumentNullException>(() => new ActingUserAccessor().Set(null!));
    }
}
