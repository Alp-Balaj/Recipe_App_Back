using RecipeApp.Application.Social.Dtos;
using RecipeApp.Application.Social.Validators;

namespace RecipeApp.UnitTests;

// Direct tests of the shared comment body rules (social-feed cp1). One validator covers
// both POST /recipes/{id}/comments and PUT /comments/{id} — they share the shape.
public class CommentRequestValidatorTests
{
    private readonly CommentRequestValidator _validator = new();

    [Fact]
    public void Validate_WellFormedRequest_IsValid()
    {
        var result = _validator.Validate(new CommentRequest("Looks delicious — tried it with basil."));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Validate_MissingContent_Fails(string? content)
    {
        var result = _validator.Validate(new CommentRequest(content!));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CommentRequest.Content));
    }

    [Fact]
    public void Validate_ContentAtMaxLength_IsValid()
    {
        var result = _validator.Validate(new CommentRequest(new string('x', 2000)));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_ContentOverMaxLength_Fails()
    {
        var result = _validator.Validate(new CommentRequest(new string('x', 2001)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CommentRequest.Content));
    }
}
