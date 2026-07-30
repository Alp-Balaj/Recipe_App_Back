using RecipeApp.Application.Moderation.Dtos;
using RecipeApp.Application.Moderation.Validators;

namespace RecipeApp.UnitTests;

public class SuspendUserRequestValidatorTests
{
    private readonly SuspendUserRequestValidator _validator = new();

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(365)]
    public void Days_in_range_pass(int days)
    {
        Assert.True(_validator.Validate(new SuspendUserRequest(days, null)).IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(366)]
    public void Days_out_of_range_fail(int days)
    {
        Assert.False(_validator.Validate(new SuspendUserRequest(days, null)).IsValid);
    }

    [Fact]
    public void Reason_over_500_chars_fails()
    {
        Assert.False(_validator.Validate(new SuspendUserRequest(7, new string('x', 501))).IsValid);
    }
}
