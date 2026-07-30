using RecipeApp.Application.Social.Dtos;
using RecipeApp.Application.Social.Validators;

namespace RecipeApp.UnitTests;

// Direct tests of the rating body rule (open-loops slice 1). The same 1-5 range is also a
// check constraint on CookedRecipes; these tests pin the 400 path so a loosened validator
// fails here rather than only as a distant 500 from the database.
public class RatingRequestValidatorTests
{
    private readonly RatingRequestValidator _validator = new();

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void Validate_RatingInRange_IsValid(int rating)
    {
        var result = _validator.Validate(new RatingRequest(rating));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(6)]
    [InlineData(int.MaxValue)]
    public void Validate_RatingOutOfRange_Fails(int rating)
    {
        var result = _validator.Validate(new RatingRequest(rating));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(RatingRequest.Rating));
    }
}
