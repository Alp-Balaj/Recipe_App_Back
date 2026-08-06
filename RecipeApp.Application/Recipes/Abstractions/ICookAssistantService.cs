using RecipeApp.Application.Recipes.Dtos;

namespace RecipeApp.Application.Recipes.Abstractions;

/// <summary>
/// Orchestration for the cook-mode assistant (stream M): who may ask, what it costs, and what
/// the model is allowed to see. The prompt and the parsing live behind
/// <see cref="ICookAssistant"/>; this decides everything the model has no business deciding.
///
/// Ordering inside the implementation is the same as the generator's, and for the same reasons:
/// visibility first (so 404 semantics are never affected by anything downstream), budget second
/// (so an exhausted allowance costs nothing), the provider call third, the usage row committed
/// with it.
/// </summary>
public interface ICookAssistantService
{
    Task<CookAssistantResult<CookAnswerResponse>> AskAsync(
        Guid recipeId,
        CookQuestionRequest request,
        Guid userId,
        CancellationToken cancellationToken = default);
}
