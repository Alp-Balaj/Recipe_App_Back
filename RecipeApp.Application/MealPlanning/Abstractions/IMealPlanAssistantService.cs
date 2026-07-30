using RecipeApp.Application.Chat.Abstractions;
using RecipeApp.Domain.Enums;

namespace RecipeApp.Application.MealPlanning.Abstractions;

// AI seam for week-plan proposals (Stream C, decision D2 = propose-then-accept). Same
// grounded-ids discipline as IChatAssistantService: the model may only assign ids from the
// candidate set, only into the open slots it was given, and everything it returns is
// re-validated server-side (hallucinated ids, unknown slots, and duplicates are dropped).
// The result is a PROPOSAL — nothing here writes MealPlanEntry rows; accepted slots go
// through the existing entry endpoint, which keeps its 409 and WeekStart rules.
//
// Candidates reuse ChatCandidateRecipe deliberately: it is the exact compact projection the
// chat lane already grounds on, and the prompt serializes it the same way.
public interface IMealPlanAssistantService
{
    Task<IReadOnlyList<ProposedSlotAssignment>> ProposeWeekAsync(
        IReadOnlyList<PlanSlot> openSlots,
        IReadOnlyList<ChatCandidateRecipe> candidates,
        IReadOnlyList<string> dietaryRestrictions,
        CancellationToken cancellationToken = default);
}

// One (day, meal) position in a week. Value equality is load-bearing: open/filled slot sets
// are HashSet<PlanSlot>.
public record PlanSlot(DayOfWeek DayOfWeek, MealType MealType);

// One filled slot in the proposal. RecipeId is always a member of the candidate set passed
// in, and (DayOfWeek, MealType) is always one of the requested open slots.
public record ProposedSlotAssignment(DayOfWeek DayOfWeek, MealType MealType, Guid RecipeId);
