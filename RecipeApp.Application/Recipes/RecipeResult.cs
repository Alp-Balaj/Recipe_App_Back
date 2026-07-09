namespace RecipeApp.Application.Recipes;

public enum RecipeOutcome { Success, NotFound, Forbidden }

// Service-layer outcome record for detail/update/delete (per the recipe-management plan's
// Decisions §5). Endpoints switch on Outcome to pick the status code; NotFound covers both
// "doesn't exist" and "not visible to the caller" so 404s never leak private recipes.
public record RecipeResult<T>(RecipeOutcome Outcome, T? Value)
{
    public static RecipeResult<T> Success(T v) => new(RecipeOutcome.Success, v);
    public static RecipeResult<T> NotFound() => new(RecipeOutcome.NotFound, default);
    public static RecipeResult<T> Forbidden() => new(RecipeOutcome.Forbidden, default);
}
