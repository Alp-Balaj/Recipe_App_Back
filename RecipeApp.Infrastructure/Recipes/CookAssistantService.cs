using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RecipeApp.Application.Chat.Abstractions;
using RecipeApp.Application.Chat.Dtos;
using RecipeApp.Application.Recipes;
using RecipeApp.Application.Recipes.Abstractions;
using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Domain.Services;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.Infrastructure.Recipes;

/// <summary>
/// POST /recipes/{id}/cook/ask — orchestration for the cook-mode assistant (stream M).
///
/// Thin by design, and the thinness is the point: with decision D14 there is no conversation to
/// create, no transcript to append to and no migration, so the only durable effect of a
/// cook-mode turn is one <c>AiUsageRecord</c> row. What is left is the ordering, which is the
/// same as the generator's because the same three things can go wrong in the same order —
/// visibility, then budget, then the provider.
/// </summary>
public class CookAssistantService : ICookAssistantService
{
    private readonly ApplicationDbContext _db;
    private readonly ICookAssistant _assistant;
    private readonly IAiUsageService _aiUsage;
    private readonly ILogger<CookAssistantService> _logger;

    public CookAssistantService(
        ApplicationDbContext db,
        ICookAssistant assistant,
        IAiUsageService aiUsage,
        ILogger<CookAssistantService> logger)
    {
        _db = db;
        _assistant = assistant;
        _aiUsage = aiUsage;
        _logger = logger;
    }

    public async Task<CookAssistantResult<CookAnswerResponse>> AskAsync(
        Guid recipeId,
        CookQuestionRequest request,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // Visibility first, and through the same predicate every other read path uses. A recipe
        // the caller may not read is NotFound, never Forbidden — cook mode does not get to be
        // the one surface that reveals a private recipe exists by refusing differently.
        //
        // AsNoTracking because nothing here writes to the recipe, and because ServingScale hands
        // back copies precisely so a scaled view can never reach a change tracker.
        var recipe = await _db.Recipes
            .AsNoTracking()
            .Where(RecipeVisibilityPolicy.VisibleTo(userId))
            .SingleOrDefaultAsync(r => r.Id == recipeId, cancellationToken);

        if (recipe is null)
        {
            return CookAssistantResult<CookAnswerResponse>.NotFound();
        }

        // Budget second: after the ownership check so 404 semantics stay untouched, and before
        // the provider call so an exhausted allowance costs nothing. Cook mode is the lane most
        // likely to be asked three questions in ninety seconds, which is exactly why it is
        // metered like the rest rather than waved through as "just a small call".
        if ((await _aiUsage.GetBudgetAsync(userId, cancellationToken)).IsExhausted)
        {
            return CookAssistantResult<CookAnswerResponse>.QuotaExceeded();
        }

        var author = await _db.Users
            .Where(u => u.Id == userId)
            .Select(u => new { u.DietaryRestrictions, u.CuisinePreferences })
            .SingleAsync(cancellationToken);

        // Scaling is done HERE, in the same arithmetic the SPA runs, so the numbers the model
        // is given are the numbers on the cook's screen. The alternative — telling the model the
        // recipe serves four and the cook wants eight — is asking a language model to multiply
        // in front of someone holding a knife.
        var targetServings = request.Servings ?? recipe.Servings;
        var scaled = ServingScale.ScaleIngredients(recipe.Ingredients, recipe.Servings, targetServings);

        var context = new CookContext(
            recipe,
            scaled,
            targetServings,
            // The named record, for stream K's reason: two adjacent string lists whose meanings
            // are opposites cannot be transposed if they are never positional. Only the
            // restrictions reach this prompt — see CookAssistant on why preferences do not.
            new AiPreferenceContext(
                author.DietaryRestrictions.Select(Vocabulary.Describe).ToList(),
                author.CuisinePreferences.Select(Vocabulary.Describe).ToList()));

        var history = (request.History ?? [])
            .Select(h => new ChatHistoryItem(h.Role, h.Content))
            .ToList();

        CookAssistantAnswer answer;
        try
        {
            answer = await _assistant.AskAsync(context, history, request.Question, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Same funnel as the other three lanes: any failure becomes AssistantUnavailable →
            // 502, with nothing billed. Cancellation propagates — a cancelled request is not an
            // assistant failure, and cook mode cancels often (the cook closed the sheet).
            _logger.LogError(ex, "Cook assistant failed for user {UserId} on recipe {RecipeId}.", userId, recipeId);
            return CookAssistantResult<CookAnswerResponse>.AssistantUnavailable();
        }

        // The one row a cook-mode turn leaves behind. A REFUSED turn is recorded too, and that
        // is not an oversight: the refusal cost a provider call, and a lane where off-topic
        // questions are free is a lane with a free-calls trick in it.
        _aiUsage.RecordCall(userId, AiUsageLanes.CookAssistant, answer.Usage);
        await _db.SaveChangesAsync(cancellationToken);

        var budget = await _aiUsage.GetBudgetAsync(userId, cancellationToken);
        return CookAssistantResult<CookAnswerResponse>.Success(new CookAnswerResponse(
            answer.Answer,
            answer.Refused,
            new AiBudgetResponse(
                budget.DailyCallLimit, budget.CallsUsed, budget.CallsRemaining,
                budget.DailyTokenLimit, budget.TokensUsed, budget.TokensRemaining,
                budget.ResetsAtUtc)));
    }
}
