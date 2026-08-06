using FluentValidation;
using RecipeApp.Application.Recipes.Dtos;

namespace RecipeApp.Application.Recipes.Validators;

/// <summary>
/// Bounds on one cook-mode turn (stream M).
///
/// Every limit here exists because decision D14 puts the transcript in the CLIENT'S hands: the
/// history is caller-supplied, it goes straight into a paid provider call, and nothing on the
/// server can cross-check it against a stored conversation the way the chat lane can. So the
/// shape is validated at the door rather than trusted — an unbounded history is an unbounded
/// token bill with an attacker holding the pen.
/// </summary>
public class CookQuestionRequestValidator : AbstractValidator<CookQuestionRequest>
{
    // Comfortably longer than any real question asked over a hot pan, short enough that it
    // cannot be used as a smuggling channel for a wall of text.
    public const int MaxQuestionLength = 500;

    // Matches the trailing window every other lane grounds on (ChatAssistantService's
    // MaxHistoryMessages, RecipeGenerationService's HistoryLimit). The assistant ALSO trims to
    // this defensively — a validator answers 400 for an honest client, and the trim is what
    // holds if this rule is ever relaxed.
    public const int MaxHistoryItems = 20;

    public const int MaxHistoryItemLength = 2000;

    public CookQuestionRequestValidator()
    {
        RuleFor(x => x.Question).NotEmpty().MaximumLength(MaxQuestionLength);

        RuleFor(x => x.History!)
            .Must(h => h.Count <= MaxHistoryItems)
            .WithMessage($"History may contain at most {MaxHistoryItems} messages.")
            .When(x => x.History is not null);

        RuleForEach(x => x.History!).ChildRules(item =>
        {
            // "user" or "assistant" and nothing else. The value is mapped onto the provider's
            // own role vocabulary one layer down, where an unexpected string is a 400 from the
            // provider — an error the caller would see as a 502 with no explanation.
            item.RuleFor(i => i.Role)
                .Must(r => r is "user" or "assistant")
                .WithMessage("Role must be 'user' or 'assistant'.");
            item.RuleFor(i => i.Content).NotEmpty().MaximumLength(MaxHistoryItemLength);
        }).When(x => x.History is not null);

        // Scaling is arithmetic and the arithmetic is bounded (ServingScale). Only checked when
        // supplied — omitting it means "the recipe as written".
        RuleFor(x => x.Servings)
            .InclusiveBetween(ServingScale.MinServings, ServingScale.MaxServings)
            .When(x => x.Servings is not null);
    }
}
