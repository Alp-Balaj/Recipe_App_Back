using RecipeApp.Application.MealPlanning.Dtos;

namespace RecipeApp.Application.MealPlanning.Abstractions;

// Orchestrates POST /meal-plans/propose-week (Stream C): loads the caller's occupied slots
// for the week, assembles the grounded candidate set (picker-corpus parity: own + saved +
// previously planned, topped up with recent public), reads dietary restrictions, invokes
// IMealPlanAssistantService, and hydrates the assignments into wire DTOs. Read-only — the
// proposal never touches MealPlanEntry rows.
//
// Outcomes: Success (possibly with an empty Slots list — a full week or an empty catalogue
// are not errors) or AssistantUnavailable (the LLM call failed; mapped to 502, mirroring
// the chat lane). Never NotFound: a missing plan for the week just means every slot is open.
public interface IMealPlanProposalService
{
    Task<MealPlanResult<ProposeWeekResponse>> ProposeWeekAsync(
        ProposeWeekRequest request,
        Guid userId,
        CancellationToken cancellationToken = default);
}
