using System.Text;
using System.Text.Json;
using RecipeApp.Application.Chat.Abstractions;
using RecipeApp.Application.MealPlanning.Abstractions;
using RecipeApp.Domain.Enums;
using RecipeApp.Infrastructure.Chat;

namespace RecipeApp.Infrastructure.MealPlanning;

// IMealPlanAssistantService over the shared IChatMessageCaller seam (Stream C). Owns the
// week-proposal prompt, the structured-output parsing, and the defensive re-validation —
// the same division of labor as ChatAssistantService: everything testable lives here, the
// network sits behind the caller seam. The grounded-ids rule extends to slots: an assignment
// survives only if its recipe id is in the candidate set AND its (day, meal) is one of the
// open slots we asked for. Everything else is dropped, silently — an empty proposal is valid.
public class MealPlanAssistantService : IMealPlanAssistantService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // Same Google-flavored OpenAPI dialect as GeminiMessageCaller's default schema (UPPERCASE
    // type enums, no additionalProperties). Day/mealType/recipeId all arrive as strings; the
    // Enum.TryParse + slot-membership + Guid.TryParse + candidate-membership filter below is
    // the real guard, so the loose schema costs nothing.
    private static readonly object ResponseSchema = new
    {
        type = "OBJECT",
        properties = new
        {
            slots = new
            {
                type = "ARRAY",
                items = new
                {
                    type = "OBJECT",
                    properties = new
                    {
                        day = new { type = "STRING" },
                        mealType = new { type = "STRING" },
                        recipeId = new { type = "STRING" },
                    },
                    required = new[] { "day", "mealType", "recipeId" },
                },
            },
        },
        required = new[] { "slots" },
    };

    private readonly IChatMessageCaller _caller;

    public MealPlanAssistantService(IChatMessageCaller caller)
    {
        _caller = caller;
    }

    public async Task<IReadOnlyList<ProposedSlotAssignment>> ProposeWeekAsync(
        IReadOnlyList<PlanSlot> openSlots,
        IReadOnlyList<ChatCandidateRecipe> candidates,
        IReadOnlyList<string> dietaryRestrictions,
        CancellationToken cancellationToken = default)
    {
        var systemPrompt = BuildSystemPrompt(openSlots, candidates, dietaryRestrictions);

        var json = await _caller.CreateJsonMessageAsync(
            systemPrompt,
            Array.Empty<ChatHistoryItem>(),
            "Plan my week.",
            ResponseSchema,
            cancellationToken);

        var parsed = ParseResponse(json);

        // Defense against hallucination: structured output guarantees the SHAPE, not semantic
        // validity. Drop any assignment whose day/mealType doesn't parse to a defined enum
        // member, whose slot wasn't requested (occupied slots stay untouched by construction),
        // whose slot was already filled earlier in the response, or whose recipe id isn't a
        // well-formed Guid from the candidate set.
        var candidateIds = candidates.Select(c => c.Id).ToHashSet();
        var openSet = openSlots.ToHashSet();
        var filled = new HashSet<PlanSlot>();
        var result = new List<ProposedSlotAssignment>();
        foreach (var raw in parsed.Slots)
        {
            if (raw?.Day is null || raw.MealType is null || raw.RecipeId is null)
            {
                continue;
            }

            // Enum.TryParse accepts bare integers ("17" parses fine), so IsDefined is required.
            if (!Enum.TryParse<DayOfWeek>(raw.Day, ignoreCase: true, out var day) || !Enum.IsDefined(day))
            {
                continue;
            }

            if (!Enum.TryParse<MealType>(raw.MealType, ignoreCase: true, out var mealType) || !Enum.IsDefined(mealType))
            {
                continue;
            }

            var slot = new PlanSlot(day, mealType);
            if (!openSet.Contains(slot) || !filled.Add(slot))
            {
                continue;
            }

            if (!Guid.TryParse(raw.RecipeId, out var recipeId) || !candidateIds.Contains(recipeId))
            {
                filled.Remove(slot);
                continue;
            }

            result.Add(new ProposedSlotAssignment(day, mealType, recipeId));
        }

        return result;
    }

    private static RawResponse ParseResponse(string json)
    {
        RawResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<RawResponse>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            // Structured output should make this impossible, but the boundary is untrusted:
            // fail clearly rather than surfacing a raw JsonException to the endpoint.
            throw new InvalidOperationException("meal-plan assistant returned malformed JSON.", ex);
        }

        if (parsed is null || parsed.Slots is null)
        {
            throw new InvalidOperationException("meal-plan assistant response was missing required fields.");
        }

        return parsed;
    }

    private static string BuildSystemPrompt(
        IReadOnlyList<PlanSlot> openSlots,
        IReadOnlyList<ChatCandidateRecipe> candidates,
        IReadOnlyList<string> dietaryRestrictions)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a meal-planning assistant for a recipe app. Fill the open slots of the user's week with recipes to cook.");
        sb.AppendLine("Choose recipes ONLY from the candidate list below — never invent recipes or recipe ids.");
        sb.AppendLine("Fill every open slot listed below when a suitable candidate exists; omit a slot from the response if nothing fits it.");
        sb.AppendLine("Prefer variety across the week. Repeating a recipe is allowed when candidates are few, but never assign the same recipe twice on the same day.");
        sb.AppendLine("Prefer quick, light recipes for Breakfast and fuller meals for Dinner.");
        sb.AppendLine("Return one slots[] object per filled slot: day is the English weekday name (e.g. \"Monday\"), mealType is \"Breakfast\", \"Lunch\" or \"Dinner\", recipeId is the exact id string of the chosen candidate.");
        sb.AppendLine();

        if (dietaryRestrictions.Count > 0)
        {
            sb.Append("Respect the user's dietary restrictions strictly: ");
            sb.AppendLine(string.Join(", ", dietaryRestrictions));
            sb.AppendLine();
        }

        sb.AppendLine("Open slots:");
        foreach (var slot in openSlots)
        {
            sb.AppendLine($"- {slot.DayOfWeek} {slot.MealType}");
        }
        sb.AppendLine();

        sb.AppendLine("Candidate recipes:");
        if (candidates.Count == 0)
        {
            sb.AppendLine("(none available — return an empty slots list)");
        }
        else
        {
            foreach (var c in candidates)
            {
                sb.AppendLine(SerializeCandidate(c));
            }
        }

        return sb.ToString();
    }

    // One compact line per candidate — identical format to ChatAssistantService's, so both
    // lanes ground the model the same way.
    private static string SerializeCandidate(ChatCandidateRecipe c)
    {
        var cuisine = string.IsNullOrWhiteSpace(c.Cuisine) ? "n/a" : c.Cuisine;
        var calories = c.Calories.HasValue ? $"{c.Calories} kcal" : "n/a";
        var tags = c.Tags.Count > 0 ? string.Join("/", c.Tags) : "none";
        return $"- id={c.Id} | {c.Title} | cuisine: {cuisine} | {c.Difficulty} | {c.TotalTimeMinutes} min | {calories} | tags: {tags} | {c.Description}";
    }

    // Mirrors the structured-output schema { slots: [{ day, mealType, recipeId }] }. Fields
    // arrive as strings (the schema can't tightly express enums-or-ids), so membership is
    // filtered in ProposeWeekAsync.
    private sealed record RawSlot(string? Day, string? MealType, string? RecipeId);

    private sealed record RawResponse(List<RawSlot?> Slots);
}
