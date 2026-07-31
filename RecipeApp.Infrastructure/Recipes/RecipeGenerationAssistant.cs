using System.Text;
using System.Text.Json;
using RecipeApp.Application.Chat.Abstractions;
using RecipeApp.Application.Recipes.Abstractions;
using RecipeApp.Domain.Entities;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;
using RecipeApp.Infrastructure.Chat;

namespace RecipeApp.Infrastructure.Recipes;

// ═══════════════════════════════════════════════════════════════════════════════════════
// THE RECIPE-CONSTRUCTION SEAM (stream E). Everything about how a generated recipe is
// BUILT lives in this one file and nowhere else:
//
//   1. the response schema        — what shape the model must return          (ResponseSchema)
//   2. the prompt                 — what the model is told a recipe is        (BuildSystemPrompt)
//   3. the parse                  — JSON text -> raw records                  (ParseResponse)
//   4. the re-validation          — raw -> normalised domain values           (Normalise*)
//   5. the entity mapping         — draft -> Recipe row                       (ToRecipe)
//
// It is deliberately one file because STREAM G RETROFITS THIS PATH. When ingredients,
// units, cuisines and tags become typed values with a resolved catalogue id, G changes the
// schema, the prompt's wording, the ingredient normaliser and ToRecipe — all five steps,
// all within these ~250 lines, instead of hunting ingredient shaping through a prompt
// builder, a parser and a mapper in three projects. Anything added here that touches how an
// ingredient, a unit, a cuisine or a tag is shaped belongs in this file too. (Execution
// plan, Wave 4 note: "keep its recipe-construction code in one place".)
//
// The trust boundary, and why this class is longer than its two sibling assistants: the
// chat recommender and the week proposer are GROUNDED — every id the model returns is
// checked against a server-built candidate set and dropped if absent. This lane is FREE:
// the model invents, so there is no set to check against. What replaces membership testing
// is RANGE testing — every field is trimmed, clamped and bounded to exactly the limits
// CreateRecipeRequestValidator enforces on the human write path, so a generated row is
// always a row a human could have typed. The user's prompt is likewise untrusted input
// that only ever reaches the model as a user turn; nothing it can say changes what this
// class does with the response, who owns the resulting recipe, or how visible it is.
// ═══════════════════════════════════════════════════════════════════════════════════════
public class RecipeGenerationAssistant : IRecipeGenerationAssistant
{
    // Trailing conversation messages handed to the model, matching ChatAssistantService's
    // cap — the caller decides how many to load, this is the defensive ceiling.
    private const int MaxHistoryMessages = 20;

    // ── Bounds (step 4). The first block mirrors CreateRecipeRequestValidator exactly;
    // the second block is this lane's own, because a validator can reject a human's absurd
    // input with a 400 but there is nobody to hand a 400 to when a model returns 900
    // servings. Everything here clamps rather than rejects, except where the value is
    // load-bearing enough that a missing one means the generation failed outright.
    private const int MaxTitleLength = 200;
    private const int MaxDescriptionLength = 2000;

    private const int MaxTimeMinutes = 24 * 60;
    private const int MaxServings = 100;
    private const int MaxCaloriesPerServing = 20_000;
    private const int MaxCuisineLength = 60;
    private const int MaxIngredients = 60;
    private const int MaxIngredientNameLength = 100;
    private const int MaxUnitLength = 30;
    private const decimal MaxQuantity = 100_000m;
    private const int MaxSteps = 40;
    private const int MaxStepDescriptionLength = 1000;
    private const int MaxTimerSeconds = 24 * 60 * 60;
    private const int MaxTags = 10;
    private const int MaxTagLength = 40;

    // The human write path requires Quantity > 0 and a non-empty Unit, so a generated
    // ingredient that has neither cannot be stored as-is. Dropping the ingredient would
    // silently corrupt the recipe (a missing "salt" is worse than an approximate one), so
    // the two get a neutral default instead. Stream G: "pcs" is the count-dimension
    // fallback — it is the value the unit enum's mapping needs to recognise.
    private const decimal FallbackQuantity = 1m;
    private const string FallbackUnit = "pcs";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // ── 1. The response schema ──────────────────────────────────────────────────────────
    // Same Google-flavoured OpenAPI dialect as GeminiMessageCaller's default schema
    // (UPPERCASE type enums, no additionalProperties). Everything scalar arrives as STRING
    // or NUMBER and is re-validated below — the schema buys shape, not meaning, and a
    // looser schema costs nothing because the normalisers are the real guard.
    private static readonly object ResponseSchema = new
    {
        type = "OBJECT",
        properties = new
        {
            title = new { type = "STRING" },
            description = new { type = "STRING" },
            prepTimeMinutes = new { type = "NUMBER" },
            cookTimeMinutes = new { type = "NUMBER" },
            servings = new { type = "NUMBER" },
            difficulty = new { type = "STRING" },
            cuisineType = new { type = "STRING" },
            caloriesPerServing = new { type = "NUMBER" },
            ingredients = new
            {
                type = "ARRAY",
                items = new
                {
                    type = "OBJECT",
                    properties = new
                    {
                        name = new { type = "STRING" },
                        quantity = new { type = "NUMBER" },
                        unit = new { type = "STRING" },
                    },
                    required = new[] { "name", "quantity", "unit" },
                },
            },
            steps = new
            {
                type = "ARRAY",
                items = new
                {
                    type = "OBJECT",
                    properties = new
                    {
                        description = new { type = "STRING" },
                        timerSeconds = new { type = "NUMBER" },
                    },
                    required = new[] { "description" },
                },
            },
            tags = new { type = "ARRAY", items = new { type = "STRING" } },
        },
        required = new[] { "title", "description", "prepTimeMinutes", "cookTimeMinutes", "servings", "difficulty", "ingredients", "steps" },
    };

    private readonly IChatMessageCaller _caller;

    public RecipeGenerationAssistant(IChatMessageCaller caller)
    {
        _caller = caller;
    }

    public async Task<GeneratedRecipe> GenerateAsync(
        string request,
        IReadOnlyList<ChatHistoryItem> history,
        IReadOnlyList<string> dietaryRestrictions,
        CancellationToken cancellationToken = default)
    {
        var trimmedHistory = history.Count > MaxHistoryMessages
            ? history.Skip(history.Count - MaxHistoryMessages).ToList()
            : history;

        // cancellationToken is NAMED here even though every argument is positional up to
        // it: the seam's 4th parameter is responseSchema (object?), and a CancellationToken
        // passed positionally binds to it silently — the bug that 502'd every chat turn on
        // 2026-07-30 (fixed in 4d2e4b8). Anything touching this seam passes it by name.
        var call = await _caller.CreateJsonMessageAsync(
            BuildSystemPrompt(dietaryRestrictions),
            trimmedHistory,
            request,
            ResponseSchema,
            cancellationToken: cancellationToken);

        var parsed = ParseResponse(call.Json);
        return new GeneratedRecipe(Normalise(parsed), call.Usage);
    }

    // ── 5. The entity mapping ───────────────────────────────────────────────────────────
    // Draft -> row. Static and in this file on purpose (see the header): stream G changes
    // the draft's ingredient shape and this is the other half of that edit. Everything the
    // model had no say in — who owns the recipe, where it came from, how visible it is,
    // that it is flagged — is supplied by the orchestrator, because provenance is not a
    // thing the model gets to assert about itself.
    public static Recipe ToRecipe(
        GeneratedRecipeDraft draft,
        Guid ownerId,
        Guid? sourceConversationId,
        RecipeVisibility visibility)
    {
        return new Recipe
        {
            Id = Guid.NewGuid(),
            Title = draft.Title,
            Description = draft.Description,
            PrepTimeMinutes = draft.PrepTimeMinutes,
            CookTimeMinutes = draft.CookTimeMinutes,
            Servings = draft.Servings,
            Difficulty = draft.Difficulty,
            CuisineType = draft.CuisineType,
            CaloriesPerServing = draft.CaloriesPerServing,
            // No image: the generator writes text. The recipe detail surface already
            // renders its generated-canvas placeholder for an imageless recipe, and the
            // owner can upload one through the normal edit path.
            ImageUrl = null,
            Visibility = visibility,
            CreatedAt = DateTime.UtcNow,
            Ingredients = draft.Ingredients,
            Steps = draft.Steps,
            Tags = draft.Tags,
            CreatedByUserId = ownerId,
            // Decision D1 (2026-07-30): user-owned, flagged, with the source conversation.
            IsAiGenerated = true,
            SourceConversationId = sourceConversationId,
        };
    }

    // ── 2. The prompt ───────────────────────────────────────────────────────────────────
    private static string BuildSystemPrompt(IReadOnlyList<string> dietaryRestrictions)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a recipe author for a cooking app. Invent ONE complete, genuinely cookable recipe that matches the user's request.");
        sb.AppendLine("Return the recipe as structured output only — no commentary, no markdown, no alternatives, exactly one recipe.");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- title: a short dish name, no more than 200 characters. Do not mention that it was generated.");
        sb.AppendLine("- description: 1-3 sentences on what the dish is and who it suits.");
        sb.AppendLine($"- prepTimeMinutes / cookTimeMinutes: whole minutes, 0 or more, each at most {MaxTimeMinutes}. Use 0 for cookTimeMinutes when nothing is cooked.");
        sb.AppendLine($"- servings: a whole number of people the quantities below feed, 1 to {MaxServings}.");
        sb.AppendLine("- difficulty: exactly one of \"Easy\", \"Medium\", \"Hard\".");
        sb.AppendLine("- cuisineType: one word or short phrase (\"Italian\", \"Thai\"); omit it when the dish belongs to no particular cuisine.");
        sb.AppendLine("- caloriesPerServing: your best estimate per serving, or omit it when you cannot estimate honestly. Never guess wildly.");
        sb.AppendLine("- ingredients: every ingredient needed, each with a name, a quantity GREATER THAN ZERO, and a unit (\"g\", \"ml\", \"tbsp\", \"cloves\", \"pcs\"). Quantities must match the servings figure. Do not put the quantity or the unit inside the name.");
        sb.AppendLine("- steps: the method in order, one instruction per step. Do not number them yourself. Set timerSeconds only on a step with a real unattended wait; omit it otherwise.");
        sb.AppendLine($"- tags: up to {MaxTags} short lowercase keywords (\"vegetarian\", \"one-pot\").");
        sb.AppendLine();
        sb.AppendLine("If the request is not about food at all, still return a sensible recipe that is as close to the request as a cooking app can honestly get.");

        if (dietaryRestrictions.Count > 0)
        {
            sb.AppendLine();
            sb.Append("The user's dietary restrictions are absolute — every ingredient must comply: ");
            sb.AppendLine(string.Join(", ", dietaryRestrictions));
        }

        return sb.ToString();
    }

    // ── 3. The parse ────────────────────────────────────────────────────────────────────
    private static RawRecipe ParseResponse(string json)
    {
        RawRecipe? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<RawRecipe>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            // Structured output should make this impossible, but the boundary is untrusted:
            // fail clearly rather than surfacing a raw JsonException to the endpoint.
            throw new InvalidOperationException("recipe generator returned malformed JSON.", ex);
        }

        if (parsed is null)
        {
            throw new InvalidOperationException("recipe generator returned no recipe.");
        }

        return parsed;
    }

    // ── 4. The re-validation ────────────────────────────────────────────────────────────
    // Everything that CAN be salvaged is clamped into range. The three things that cannot
    // be invented on the model's behalf — a title, at least one ingredient, at least one
    // step — throw instead, which the orchestrator turns into a 502 with nothing persisted.
    private static GeneratedRecipeDraft Normalise(RawRecipe raw)
    {
        var title = Clip(raw.Title, MaxTitleLength);
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new InvalidOperationException("recipe generator returned a recipe with no title.");
        }

        var ingredients = NormaliseIngredients(raw.Ingredients);
        if (ingredients.Count == 0)
        {
            throw new InvalidOperationException("recipe generator returned a recipe with no usable ingredients.");
        }

        var steps = NormaliseSteps(raw.Steps);
        if (steps.Count == 0)
        {
            throw new InvalidOperationException("recipe generator returned a recipe with no usable steps.");
        }

        // A missing description is recoverable — the title always exists by now, and an
        // empty string would fail the same NotEmpty rule the human path enforces.
        var description = Clip(raw.Description, MaxDescriptionLength);
        if (string.IsNullOrWhiteSpace(description))
        {
            description = title;
        }

        // Enum.TryParse accepts bare integers ("17" parses fine), so IsDefined is required.
        var difficulty = Enum.TryParse<DifficultyLevel>(raw.Difficulty, ignoreCase: true, out var parsedDifficulty)
            && Enum.IsDefined(parsedDifficulty)
                ? parsedDifficulty
                : DifficultyLevel.Medium;

        return new GeneratedRecipeDraft(
            title,
            description,
            ClampInt(raw.PrepTimeMinutes, 0, MaxTimeMinutes) ?? 0,
            ClampInt(raw.CookTimeMinutes, 0, MaxTimeMinutes) ?? 0,
            ClampInt(raw.Servings, 1, MaxServings) ?? 1,
            difficulty,
            Clip(raw.CuisineType, MaxCuisineLength) is { Length: > 0 } cuisine ? cuisine : null,
            // Calories are the one field where a wrong number is worse than no number: they
            // feed the day page's totals and the week's calorie ribbon. A zero or negative
            // estimate is the model declining, so it drops to null instead of clamping up
            // into a fake "1 kcal".
            DropBelowMinimum(raw.CaloriesPerServing, 1, MaxCaloriesPerServing),
            ingredients,
            steps,
            NormaliseTags(raw.Tags));
    }

    private static List<RecipeIngredient> NormaliseIngredients(List<RawIngredient?>? raw)
    {
        var result = new List<RecipeIngredient>();
        if (raw is null)
        {
            return result;
        }

        foreach (var item in raw)
        {
            if (result.Count >= MaxIngredients)
            {
                break;
            }

            var name = Clip(item?.Name, MaxIngredientNameLength);
            if (string.IsNullOrWhiteSpace(name))
            {
                // An ingredient with no name carries no information at all — this is the
                // one case where dropping the row loses nothing.
                continue;
            }

            var unit = Clip(item?.Unit, MaxUnitLength);
            var quantity = item?.Quantity is decimal q && q > 0m ? Math.Min(q, MaxQuantity) : FallbackQuantity;

            result.Add(new RecipeIngredient
            {
                Name = name,
                Quantity = quantity,
                Unit = string.IsNullOrWhiteSpace(unit) ? FallbackUnit : unit,
            });
        }

        return result;
    }

    private static List<RecipeStep> NormaliseSteps(List<RawStep?>? raw)
    {
        var result = new List<RecipeStep>();
        if (raw is null)
        {
            return result;
        }

        foreach (var item in raw)
        {
            if (result.Count >= MaxSteps)
            {
                break;
            }

            var description = Clip(item?.Description, MaxStepDescriptionLength);
            if (string.IsNullOrWhiteSpace(description))
            {
                continue;
            }

            result.Add(new RecipeStep
            {
                // Renumbered from the surviving steps, ignoring whatever the model sent:
                // that guarantees StepNumber > 0 with no gaps, duplicates or reordering,
                // which is what the human path's validator and the detail page assume.
                StepNumber = result.Count + 1,
                Description = description,
                // 0 / absent both mean "no unattended wait here", which is null — the same
                // reasoning as calories, and what the entity's own comment asks for.
                TimerSeconds = DropBelowMinimum(item?.TimerSeconds, 1, MaxTimerSeconds),
            });
        }

        return result;
    }

    private static List<string> NormaliseTags(List<string?>? raw)
    {
        var result = new List<string>();
        if (raw is null)
        {
            return result;
        }

        // Case-insensitive dedupe, original casing preserved: tag FILTERING is
        // case-sensitive (GET /recipes), so "Vegan" and "vegan" on one recipe would be two
        // separate facets of the same idea.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in raw)
        {
            if (result.Count >= MaxTags)
            {
                break;
            }

            var tag = Clip(item, MaxTagLength);
            if (!string.IsNullOrWhiteSpace(tag) && seen.Add(tag))
            {
                result.Add(tag);
            }
        }

        return result;
    }

    // Trim, then cut to length. Null-safe, and returns "" rather than null so the callers
    // can use one IsNullOrWhiteSpace test for "absent" and "blank" alike.
    private static string Clip(string? value, int maxLength)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength].TrimEnd();
    }

    // The model returns JSON numbers, which may be fractional ("2.5 servings") or absurd.
    // Round, clamp into range, and treat NaN/infinity (which System.Text.Json can produce
    // from a malformed literal) as absent. Null in, null out.
    private static int? ClampInt(double? value, int min, int max)
    {
        if (value is not double v || double.IsNaN(v) || double.IsInfinity(v))
        {
            return null;
        }

        var rounded = Math.Round(v, MidpointRounding.AwayFromZero);
        if (rounded < min)
        {
            return min;
        }

        return rounded > max ? max : (int)rounded;
    }

    // Same rounding and upper clamp, but a value below `min` becomes null rather than
    // being pulled up to it. For the two OPTIONAL numbers (calories, timer seconds),
    // "absent" is a meaningful answer and clamping would manufacture a false one.
    private static int? DropBelowMinimum(double? value, int min, int max)
    {
        var clamped = ClampInt(value, min, max);
        if (clamped is null || value is not double v || double.IsNaN(v))
        {
            return null;
        }

        return Math.Round(v, MidpointRounding.AwayFromZero) < min ? null : clamped;
    }

    // Mirrors the structured-output schema. Every field is nullable and loosely typed on
    // purpose — this record is the untrusted boundary, and Normalise above is where the
    // shapes become domain values.
    private sealed record RawIngredient(string? Name, decimal? Quantity, string? Unit);

    private sealed record RawStep(string? Description, double? TimerSeconds);

    private sealed record RawRecipe(
        string? Title,
        string? Description,
        double? PrepTimeMinutes,
        double? CookTimeMinutes,
        double? Servings,
        string? Difficulty,
        string? CuisineType,
        double? CaloriesPerServing,
        List<RawIngredient?>? Ingredients,
        List<RawStep?>? Steps,
        List<string?>? Tags);
}
