using RecipeApp.Application.Social.Dtos;
using RecipeApp.Application.Social.Validators;
using RecipeApp.Domain.Enums;

namespace RecipeApp.UnitTests;

// Direct tests of the PUT /users/me validation rules — each constraint asserted in isolation
// so a changed rule fails a named test rather than only a distant 400.
public class UpdateProfileRequestValidatorTests
{
    private readonly UpdateProfileRequestValidator _validator = new();

    private static UpdateProfileRequest Valid() =>
        new("validuser", "A short bio.", "/images/pic.jpg", RecipeVisibility.Public);

    [Fact]
    public void Validate_WellFormedRequest_IsValid()
    {
        Assert.True(_validator.Validate(Valid()).IsValid);
    }

    [Fact]
    public void Validate_NullOptionalFields_IsValid()
    {
        Assert.True(_validator.Validate(Valid() with { Bio = null, ProfileImageUrl = null }).IsValid);
    }

    [Theory]
    [InlineData("")]    // NotEmpty
    [InlineData("ab")]  // MinimumLength(3)
    public void Validate_ShortOrEmptyUsername_FailsOnUsername(string username)
    {
        var result = _validator.Validate(Valid() with { Username = username });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(UpdateProfileRequest.Username));
    }

    [Fact]
    public void Validate_UsernameOver50Chars_FailsOnUsername()
    {
        var result = _validator.Validate(Valid() with { Username = new string('a', 51) });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(UpdateProfileRequest.Username));
    }

    [Fact]
    public void Validate_BioOver160Chars_FailsOnBio()
    {
        var result = _validator.Validate(Valid() with { Bio = new string('x', 161) });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(UpdateProfileRequest.Bio));
    }

    [Fact]
    public void Validate_UnknownVisibility_FailsOnVisibility()
    {
        var result = _validator.Validate(Valid() with { DefaultRecipeVisibility = (RecipeVisibility)99 });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(UpdateProfileRequest.DefaultRecipeVisibility));
    }
}
