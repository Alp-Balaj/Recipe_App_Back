using RecipeApp.Application.Auth.Dtos;
using RecipeApp.Application.Auth.Validators;

namespace RecipeApp.UnitTests;

// Direct tests of the FluentValidation rules (audit 4.5): the validator is exercised end to
// end through the endpoint filter elsewhere; here each rule is asserted in isolation so a
// changed constraint fails a named test rather than only a distant 400 assertion.
public class RegisterRequestValidatorTests
{
    private readonly RegisterRequestValidator _validator = new();

    private static RegisterRequest Valid() => new("validuser", "valid@example.com", "Password123!");

    [Fact]
    public void Validate_WellFormedRequest_IsValid()
    {
        Assert.True(_validator.Validate(Valid()).IsValid);
    }

    [Theory]
    [InlineData("")]     // NotEmpty
    [InlineData("ab")]   // MinimumLength(3)
    public void Validate_ShortOrEmptyUsername_FailsOnUsername(string username)
    {
        var result = _validator.Validate(Valid() with { Username = username });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(RegisterRequest.Username));
    }

    [Fact]
    public void Validate_UsernameOver50Chars_FailsOnUsername()
    {
        var result = _validator.Validate(Valid() with { Username = new string('a', 51) });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(RegisterRequest.Username));
    }

    [Theory]
    [InlineData("")]              // NotEmpty
    [InlineData("not-an-email")]  // EmailAddress
    public void Validate_BadEmail_FailsOnEmail(string email)
    {
        var result = _validator.Validate(Valid() with { Email = email });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(RegisterRequest.Email));
    }

    [Theory]
    [InlineData("")]         // NotEmpty
    [InlineData("short")]    // MinimumLength(8)
    public void Validate_ShortPassword_FailsOnPassword(string password)
    {
        var result = _validator.Validate(Valid() with { Password = password });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(RegisterRequest.Password));
    }
}
