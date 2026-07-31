using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Application.Recipes.Validators;
using RecipeApp.Domain.Enums;

namespace RecipeApp.UnitTests;

// Stream E. The prompt is the only free-text field on the generation request and it is
// injected verbatim into a PAID provider call, so its bound is a cost control, not a
// nicety — the same reasoning that caps CreateRecipeRequest.Description.
public class GenerateRecipeRequestValidatorTests
{
    private readonly GenerateRecipeRequestValidator _validator = new();

    private static GenerateRecipeRequest Request(
        string prompt = "something with cod",
        Guid? conversationId = null,
        RecipeVisibility? visibility = null) => new(prompt, conversationId, visibility);

    [Fact]
    public void ValidRequest_Passes()
    {
        Assert.True(_validator.Validate(Request()).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankPrompt_Fails(string prompt)
    {
        Assert.False(_validator.Validate(Request(prompt)).IsValid);
    }

    [Fact]
    public void PromptAtTheLimit_Passes()
    {
        var prompt = new string('a', GenerateRecipeRequestValidator.MaxPromptLength);

        Assert.True(_validator.Validate(Request(prompt)).IsValid);
    }

    [Fact]
    public void PromptOverTheLimit_Fails()
    {
        var prompt = new string('a', GenerateRecipeRequestValidator.MaxPromptLength + 1);

        Assert.False(_validator.Validate(Request(prompt)).IsValid);
    }

    [Fact]
    public void OmittedVisibility_Passes()
    {
        // Omitted means "use my DefaultRecipeVisibility" — the service resolves it, so the
        // validator must not treat null as an undefined enum value.
        Assert.True(_validator.Validate(Request(visibility: null)).IsValid);
    }

    [Fact]
    public void SuppliedVisibility_MustBeDefined()
    {
        Assert.True(_validator.Validate(Request(visibility: RecipeVisibility.Private)).IsValid);
        Assert.False(_validator.Validate(Request(visibility: (RecipeVisibility)99)).IsValid);
    }

    [Fact]
    public void ConversationId_IsOptional()
    {
        Assert.True(_validator.Validate(Request(conversationId: null)).IsValid);
        Assert.True(_validator.Validate(Request(conversationId: Guid.NewGuid())).IsValid);
    }
}
