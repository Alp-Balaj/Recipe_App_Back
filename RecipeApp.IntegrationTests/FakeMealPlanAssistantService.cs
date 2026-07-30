using RecipeApp.Application.Chat.Abstractions;
using RecipeApp.Application.MealPlanning.Abstractions;

namespace RecipeApp.IntegrationTests;

// Deterministic stand-in for the Gemini-backed IMealPlanAssistantService (Stream C) so CI
// never calls the real API. Same design rules as FakeChatAssistantService: behaviour is a
// PURE function of the inputs — no mutable state — so it's safe under the shared TestServer:
//
//   * Every open slot is filled, round-robin over the candidate list in order. This honours
//     the assignments ⊆ (open slots × candidate ids) contract the real service enforces.
//   * A candidate whose title contains the sentinel "__FAIL__" throws, simulating an LLM
//     failure so the 502 path can be exercised. The sentinel recipe must be PRIVATE to the
//     test's user — public recipes enter every caller's candidate set on the shared DB.
public sealed class FakeMealPlanAssistantService : IMealPlanAssistantService
{
    public const string FailSentinel = "__FAIL__";

    public Task<IReadOnlyList<ProposedSlotAssignment>> ProposeWeekAsync(
        IReadOnlyList<PlanSlot> openSlots,
        IReadOnlyList<ChatCandidateRecipe> candidates,
        IReadOnlyList<string> dietaryRestrictions,
        CancellationToken cancellationToken = default)
    {
        if (candidates.Any(c => c.Title.Contains(FailSentinel, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Simulated meal-plan assistant failure.");
        }

        var assignments = new List<ProposedSlotAssignment>(openSlots.Count);
        for (var i = 0; i < openSlots.Count; i++)
        {
            var slot = openSlots[i];
            assignments.Add(new ProposedSlotAssignment(
                slot.DayOfWeek, slot.MealType, candidates[i % candidates.Count].Id));
        }

        return Task.FromResult<IReadOnlyList<ProposedSlotAssignment>>(assignments);
    }
}
