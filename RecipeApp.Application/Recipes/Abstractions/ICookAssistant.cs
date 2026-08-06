using RecipeApp.Application.Chat.Abstractions;
using RecipeApp.Domain.Entities;

namespace RecipeApp.Application.Recipes.Abstractions;

/// <summary>
/// The AI seam for stream M's cook-mode assistant — the fourth assistant, and the narrowest.
///
/// Like its three siblings the implementation owns the prompt, the structured-output schema and
/// the parsing, while the network call sits one level lower behind <c>IChatMessageCaller</c>, so
/// everything interesting here is unit-testable without a provider.
///
/// ── WHAT MAKES THIS ONE DIFFERENT: THE CONTEXT IS CLOSED ──────────────────────────────────
/// The chat recommender is grounded on a candidate SET the server built and drops any id
/// outside it. The generator is free and re-validates ranges instead. This one is grounded on
/// exactly ONE recipe, and the interesting property is not what it may cite but what it may
/// talk about at all: a cook standing over a pan asked a question about THIS dish, and an
/// assistant that will also explain the offside rule is an assistant nobody can predict.
///
/// So <see cref="CookAssistantAnswer.Refused"/> is part of the contract rather than a tone the
/// prompt hopes for. The model classifies its own answer, the flag rides the wire, and both the
/// UI and the tests can assert on refusal instead of reading prose and guessing. "Demonstrable"
/// is the whole point: a refusal you cannot assert on is a refusal you do not have.
///
/// ── WHAT IT IS NOT ALLOWED TO DO ──────────────────────────────────────────────────────────
/// Arithmetic. Serving scaling is <see cref="ServingScale"/>, on both sides of the wire, and
/// the quantities in the prompt are ALREADY scaled to the cook's chosen serving count before
/// this seam sees them. The model is told what the numbers are; it is never asked to work them
/// out. A language model doing multiplication in front of someone holding a knife is a
/// correctness problem with a physical consequence.
/// </summary>
public interface ICookAssistant
{
    /// <summary>
    /// Answers one cook-mode question about one recipe.
    ///
    /// <paramref name="history"/> is the exchange so far, supplied BY THE CLIENT per decision
    /// D14 — nothing about a cook-mode turn is persisted, so there is no conversation to read
    /// it back from. Throws <see cref="InvalidOperationException"/> when the model's output
    /// cannot be parsed; the orchestrator funnels that to AssistantUnavailable, exactly as the
    /// other three lanes do.
    /// </summary>
    Task<CookAssistantAnswer> AskAsync(
        CookContext context,
        IReadOnlyList<ChatHistoryItem> history,
        string question,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Everything the assistant is allowed to know, assembled by the orchestrator.
///
/// <paramref name="Recipe"/> arrives whole because the prompt needs its method, and
/// <paramref name="ScaledIngredients"/> arrives SEPARATELY rather than as a mutated recipe:
/// scaling is a view, the entity is tracked, and the two facts must not be conflated by a type
/// that lets a caller hand the change tracker a doubled recipe by accident.
/// </summary>
public record CookContext(
    Recipe Recipe,
    IReadOnlyList<Domain.ValueObjects.RecipeIngredient> ScaledIngredients,
    int TargetServings,
    AiPreferenceContext Preferences);

/// <summary>
/// One answer. <paramref name="Refused"/> is the model's own verdict that the question was not
/// about this recipe or about cooking it; when it is true the answer is the refusal itself, so
/// the surface renders one thing either way and never has to synthesise a refusal of its own.
/// </summary>
public record CookAssistantAnswer(string Answer, bool Refused, ChatTokenUsage? Usage);
