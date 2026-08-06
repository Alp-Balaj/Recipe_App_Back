using RecipeApp.Application.Chat.Abstractions;
using RecipeApp.Application.Recipes.Abstractions;

namespace RecipeApp.IntegrationTests;

// Deterministic stand-in for the Gemini-backed ICookAssistant (stream M) so CI never calls the
// real API. Same design rules as the other three fakes: behaviour is a PURE function of the
// inputs — no mutable state — so it is safe under the shared TestServer.
//
// What it echoes into the answer is chosen so an integration test can prove the ORCHESTRATOR
// did its job, which is the only part of this lane the integration suite owns:
//
//   * the target serving count and the first scaled quantity, which together prove that
//     ServingScale ran on the server and that the scaled list — not the stored one — is what
//     reached the seam;
//   * the caller's dietary restrictions, proving the account was actually loaded rather than
//     an empty AiPreferenceContext passed through;
//   * the history length, proving the client-supplied transcript (decision D14) survived the
//     wire and the trim.
//
// A question containing "__OFFTOPIC__" comes back refused, and one containing "__FAIL__"
// throws — the 502 path with nothing billed. Neither is a judgement about the real model's
// refusal behaviour: the PROMPT is what makes refusal happen and CookAssistantTests asserts on
// it, while this fake only has to prove the flag survives the orchestrator and the wire.
public sealed class FakeCookAssistant : ICookAssistant
{
    public const string FailSentinel = "__FAIL__";
    public const string RefuseSentinel = "__OFFTOPIC__";

    public Task<CookAssistantAnswer> AskAsync(
        CookContext context,
        IReadOnlyList<ChatHistoryItem> history,
        string question,
        CancellationToken cancellationToken = default)
    {
        if (question.Contains(FailSentinel, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Simulated cook assistant failure.");
        }

        if (question.Contains(RefuseSentinel, StringComparison.Ordinal))
        {
            return Task.FromResult(new CookAssistantAnswer(
                "I can only help with this recipe while you're cooking it.",
                Refused: true,
                new ChatTokenUsage(80, 20, 100)));
        }

        var restrictions = context.Preferences.DietaryRestrictions.Count == 0
            ? "none"
            : string.Join('/', context.Preferences.DietaryRestrictions);
        var firstQuantity = context.ScaledIngredients.Count == 0
            ? "none"
            : context.ScaledIngredients[0].Quantity.ToString("0.##");

        return Task.FromResult(new CookAssistantAnswer(
            $"Answered. servings-{context.TargetServings} first-{firstQuantity} " +
            $"restrictions-{restrictions} history-{history.Count}",
            Refused: false,
            new ChatTokenUsage(150, 50, 200)));
    }
}
