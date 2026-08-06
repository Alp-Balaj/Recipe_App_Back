using System.Text;
using System.Text.Json;
using RecipeApp.Application.Chat.Abstractions;
using RecipeApp.Application.Recipes.Abstractions;
using RecipeApp.Application.Recipes.Import;
using RecipeApp.Application.Recipes.Validators;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.Services;
using RecipeApp.Domain.ValueObjects;
using RecipeApp.Infrastructure.Chat;

namespace RecipeApp.Infrastructure.Recipes;

// ═══════════════════════════════════════════════════════════════════════════════════════════
// THE EXTRACTION CONSTRUCTION SEAM (stream L), built to the same one-file discipline as
// RecipeGenerationAssistant: the schema, the prompt, the parse and the re-validation live
// together, because a change to any one of them is usually a change to the others.
//
// ── HOW THIS DIFFERS FROM THE GENERATOR, WHICH IS THE WHOLE DESIGN ────────────────────────
// The generator INVENTS and this one TRANSCRIBES, and almost every decision below follows from
// that one sentence.
//
//   * The generator's trust boundary is RANGE testing — clamp everything, because a model
//     asked to invent has no source to be unfaithful to and a value out of range is simply
//     nonsense. Clamping is a repair.
//   * This one has a source. A value out of range means the model MISREAD something, and
//     clamping it would replace a detectable error with a plausible lie. So this class parses,
//     bounds-checks and DROPS — it never repairs. A dropped field is visibly absent; a clamped
//     one is invisibly wrong, and the user is looking at a recipe they believe is the one on
//     the page.
//
// That is also why almost every field in ImportedRecipeDraft is nullable where
// GeneratedRecipeDraft's is not: "the source did not say" is a real answer here and there is
// nobody to invent one on the source's behalf.
//
// ── THE PROMPT'S ONE JOB ─────────────────────────────────────────────────────────────────
// Stop the model helping. A language model asked to read a recipe will, unprompted, round
// quantities to nicer numbers, rewrite terse instructions into fuller sentences, add a
// preheat step the author left implicit, and translate regional terms. Every one of those
// makes the extraction read BETTER and makes it a different recipe from the one at the source
// URL — which D15 promises the reader they can go and check. The prompt therefore spends most
// of its length forbidding improvement.
// ═══════════════════════════════════════════════════════════════════════════════════════════
public class RecipeExtractionAssistant : IRecipeExtractionAssistant
{
    private const int MaxTitleLength = 200;
    private const int MaxDescriptionLength = 2000;
    private const int MaxIngredientNameLength = 100;
    private const int MaxStepDescriptionLength = 1000;
    private const int MaxIngredients = 100;
    private const int MaxSteps = 60;
    private const int MaxTags = 10;
    private const int MaxTimeMinutes = 24 * 60;
    private const int MaxServings = 100;
    private const int MaxCaloriesPerServing = 20_000;

    private static readonly string[] UnitNames = Enum.GetNames<UnitOfMeasure>();
    private static readonly string[] CuisineNames = Enum.GetNames<Cuisine>();
    private static readonly string[] TagNames = Enum.GetNames<RecipeTag>();
    private static readonly string[] TemperatureUnitNames = Enum.GetNames<TemperatureUnit>();
    private static readonly string[] DifficultyNames = Enum.GetNames<DifficultyLevel>();

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // Same Google-flavoured OpenAPI dialect as the other two schemas in this codebase
    // (UPPERCASE type names, no additionalProperties). The three closed vocabularies carry an
    // `enum` constraint for the reason stream G established: it is the one place the schema can
    // buy MEANING rather than shape, so "cups" cannot come back at all.
    //
    // NOTHING IS REQUIRED except the two lists. The generator marks eight fields required
    // because a generated recipe with no servings figure is an incomplete invention; an
    // EXTRACTED one with no servings figure is an accurate reading of a page that did not state
    // it. Marking those required would push the model to invent exactly the values this class
    // exists to avoid inventing.
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
            difficulty = new { type = "STRING", @enum = DifficultyNames },
            cuisineType = new { type = "STRING", @enum = CuisineNames },
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
                        unit = new { type = "STRING", @enum = UnitNames },
                    },
                    required = new[] { "name" },
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
                        durationSeconds = new { type = "NUMBER" },
                        ingredientIndexes = new { type = "ARRAY", items = new { type = "NUMBER" } },
                        temperature = new
                        {
                            type = "OBJECT",
                            properties = new
                            {
                                value = new { type = "NUMBER" },
                                unit = new { type = "STRING", @enum = TemperatureUnitNames },
                            },
                            required = new[] { "value", "unit" },
                        },
                    },
                    required = new[] { "description" },
                },
            },
            tags = new { type = "ARRAY", items = new { type = "STRING", @enum = TagNames } },
        },
        required = new[] { "ingredients", "steps" },
    };

    private readonly IChatMessageCaller _textCaller;
    private readonly IVisionMessageCaller _visionCaller;

    public RecipeExtractionAssistant(IChatMessageCaller textCaller, IVisionMessageCaller visionCaller)
    {
        _textCaller = textCaller;
        _visionCaller = visionCaller;
    }

    public async Task<ExtractedRecipe> ExtractFromPageAsync(
        string pageText,
        string? sourceUrl,
        CancellationToken cancellationToken = default)
    {
        var userMessage = string.IsNullOrWhiteSpace(sourceUrl)
            ? pageText
            : $"Source: {sourceUrl}\n\n{pageText}";

        // cancellationToken is NAMED. The seam's fourth parameter is responseSchema (object?),
        // and a CancellationToken passed positionally binds to it silently — the bug that 502'd
        // every chat turn on 2026-07-30. Anything touching this seam passes it by name.
        var call = await _textCaller.CreateJsonMessageAsync(
            BuildPrompt(TranscriptionSource.WebPage),
            [],
            userMessage,
            ResponseSchema,
            cancellationToken: cancellationToken);

        return new ExtractedRecipe(Normalise(Parse(call.Json)), call.Usage);
    }

    public async Task<ExtractedRecipe> ExtractFromImagesAsync(
        IReadOnlyList<RecipeImageContent> images,
        CancellationToken cancellationToken = default)
    {
        var parts = images.Select(i => new VisionImagePart(i.Content, i.ContentType)).ToList();

        var call = await _visionCaller.CreateJsonMessageFromImagesAsync(
            BuildPrompt(TranscriptionSource.Photograph),
            parts,
            "Transcribe the recipe shown in these images.",
            ResponseSchema,
            cancellationToken: cancellationToken);

        return new ExtractedRecipe(Normalise(Parse(call.Json)), call.Usage);
    }

    private enum TranscriptionSource
    {
        WebPage,
        Photograph,
    }

    // ── The prompt ──────────────────────────────────────────────────────────────────────
    private static string BuildPrompt(TranscriptionSource source)
    {
        var sb = new StringBuilder();

        sb.AppendLine("You are transcribing a recipe that someone else wrote. You are NOT writing a recipe.");
        sb.AppendLine();
        sb.AppendLine("THE ONE RULE: reproduce what the source says. Do not improve it.");
        sb.AppendLine("Specifically, and these are the mistakes that matter:");
        sb.AppendLine("- Never round or 'tidy' a quantity. 385 g stays 385 g; it does not become 400 g.");
        sb.AppendLine("- Never rewrite an instruction into better prose. Keep the author's wording and their order.");
        sb.AppendLine("- Never add a step the source does not contain, even an obvious one such as preheating an oven or seasoning to taste.");
        sb.AppendLine("- Never add an ingredient the source does not list, even one the method clearly needs.");
        sb.AppendLine("- Never translate, localise or modernise a term. Keep regional names as written.");
        sb.AppendLine("- If a field is not stated, OMIT IT. Do not estimate it, and do not infer it from the other fields.");
        sb.AppendLine("  A missing prep time, serving count, calorie figure or cuisine is a correct answer.");
        sb.AppendLine();

        if (source == TranscriptionSource.WebPage)
        {
            sb.AppendLine("The input is the visible text of a web page, so it also contains navigation, adverts,");
            sb.AppendLine("the author's personal preamble, comments and other recipes' titles. Take ONLY the single");
            sb.AppendLine("main recipe the page is about. Ignore reader comments entirely, including any that suggest");
            sb.AppendLine("changes — a commenter's variation is not this recipe.");
        }
        else
        {
            sb.AppendLine("The input is one or more photographs of a WRITTEN recipe — a cookbook page, a screenshot,");
            sb.AppendLine("a handwritten card. Read the text in the images.");
            sb.AppendLine("If handwriting is genuinely illegible, leave that part out rather than guessing at it.");
            sb.AppendLine("If the images show FOOD rather than a written recipe, return empty ingredients and steps —");
            sb.AppendLine("do not invent a recipe for the dish you can see.");
        }

        sb.AppendLine();
        sb.AppendLine("Field rules:");
        sb.AppendLine($"- title: the recipe's name as written, at most {MaxTitleLength} characters.");
        sb.AppendLine("- description: the source's own summary if it has one. If it does not, omit it — do not write one.");
        sb.AppendLine($"- prepTimeMinutes / cookTimeMinutes: whole minutes, only if stated. At most {MaxTimeMinutes}.");
        sb.AppendLine($"- servings: only if the source states a yield. 1 to {MaxServings}.");
        sb.AppendLine($"- difficulty: EXACTLY one of [{string.Join(", ", DifficultyNames)}], and only if the source says so. Omit it otherwise — do not judge it yourself.");
        sb.AppendLine($"- cuisineType: EXACTLY one of [{string.Join(", ", CuisineNames)}], only when the source makes it plain. Omit rather than approximate.");
        sb.AppendLine("- caloriesPerServing: only if the source gives a figure. Never estimate one.");
        sb.AppendLine($"- ingredients: every line, in the source's order, each with a name and — where stated — a quantity and a unit that is EXACTLY one of [{string.Join(", ", UnitNames)}].");
        sb.AppendLine("  Keep the name as written, including preparation notes such as \"softened\" or \"finely chopped\".");
        sb.AppendLine("  Do not put the quantity or the unit inside the name. If a line gives no amount (\"salt to taste\"), omit quantity and unit.");
        sb.AppendLine("- steps: the method in order, one instruction per step, in the source's words. Do not number them yourself.");
        sb.AppendLine($"- steps[].durationSeconds: only when the step states a time. Whole seconds, at most {RecipeStepRules.MaxDurationSeconds}.");
        sb.AppendLine("- steps[].ingredientIndexes: the 0-based positions, in the ingredients array you just returned, of the ingredients THIS step uses.");
        sb.AppendLine("  A step that uses none (\"preheat the oven\") omits it. Never list a position twice. Never list every ingredient on every step.");
        sb.AppendLine("  Only include a position when the step genuinely refers to that ingredient — if you are unsure, leave it out.");
        sb.AppendLine($"- steps[].temperature: {{ value, unit }} with unit EXACTLY one of [{string.Join(", ", TemperatureUnitNames)}], only when the source states a temperature WITH its scale.");
        sb.AppendLine("  If the source writes a bare number of degrees with no C or F, omit the temperature entirely rather than choosing a scale.");
        sb.AppendLine($"- tags: up to {MaxTags}, each EXACTLY one of [{string.Join(", ", TagNames)}], only where genuinely true of the dish. An empty list is fine.");
        sb.AppendLine();
        sb.AppendLine("If the input contains no recipe at all, return empty ingredients and empty steps. Do not invent one.");

        return sb.ToString();
    }

    // ── The parse ───────────────────────────────────────────────────────────────────────
    private static RawRecipe Parse(string json)
    {
        RawRecipe? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<RawRecipe>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("recipe extractor returned malformed JSON.", ex);
        }

        return parsed ?? throw new InvalidOperationException("recipe extractor returned nothing.");
    }

    // ── The re-validation ───────────────────────────────────────────────────────────────
    // Drops rather than repairs — see the class header. The orchestrator turns an empty result
    // into NotARecipe, and a throw into AssistantUnavailable.
    private static ImportedRecipeDraft Normalise(RawRecipe raw)
    {
        var (ingredients, indexMap) = NormaliseIngredients(raw.Ingredients);
        var steps = NormaliseSteps(raw.Steps, indexMap);

        return new ImportedRecipeDraft
        {
            Title = Clip(raw.Title, MaxTitleLength),
            Description = Clip(raw.Description, MaxDescriptionLength),
            PrepTimeMinutes = InRange(raw.PrepTimeMinutes, 0, MaxTimeMinutes),
            CookTimeMinutes = InRange(raw.CookTimeMinutes, 0, MaxTimeMinutes),
            Servings = InRange(raw.Servings, 1, MaxServings),
            Difficulty = Vocabulary.TryParseMember<DifficultyLevel>(raw.Difficulty, out var difficulty)
                ? difficulty
                : null,
            CuisineType = Vocabulary.TryParseMember<Cuisine>(raw.CuisineType, out var cuisine) ? cuisine : null,
            CaloriesPerServing = InRange(raw.CaloriesPerServing, 1, MaxCaloriesPerServing),
            Ingredients = ingredients,
            Steps = steps,
            Tags = NormaliseTags(raw.Tags),
            // The model is never asked for an image and could not supply one. The page's own
            // image comes from the JSON-LD parser or from nowhere.
            ImageUrl = null,
        };
    }

    /// <summary>
    /// Surviving lines plus a map from the model's own position to the final one, or -1 where
    /// the line was dropped. Identical in purpose to the generator's: the steps reference lines
    /// BY POSITION (D16), so anything that moves the lines has to move the references.
    /// </summary>
    private static (List<RecipeIngredient> Ingredients, int[] IndexMap) NormaliseIngredients(List<RawIngredient?>? raw)
    {
        var result = new List<RecipeIngredient>();
        if (raw is null)
        {
            return (result, []);
        }

        var indexMap = new int[raw.Count];
        Array.Fill(indexMap, -1);

        for (var i = 0; i < raw.Count; i++)
        {
            if (result.Count >= MaxIngredients)
            {
                break;
            }

            var name = Clip(raw[i]?.Name, MaxIngredientNameLength);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var quantity = raw[i]?.Quantity;
            var hasUnit = Units.TryParse(raw[i]?.Unit, out var unit);

            indexMap[i] = result.Count;
            result.Add(new RecipeIngredient
            {
                Name = name,
                // No amount stated means the author did not measure it. ToTaste (Imprecise
                // dimension) is what stops the shopping list summing a quantity nobody wrote —
                // the same reasoning IngredientLineParser applies on the deterministic path,
                // so both tiers encode "unmeasured" identically.
                Quantity = quantity is > 0m and <= 100_000m ? quantity.Value : 1m,
                Unit = hasUnit ? unit : (quantity is > 0m ? UnitOfMeasure.Piece : UnitOfMeasure.ToTaste),
            });
        }

        return (result, indexMap);
    }

    private static List<RecipeStep> NormaliseSteps(List<RawStep?>? raw, int[] indexMap)
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
                StepNumber = result.Count + 1,
                // D15: whatever the model returned as the method IS the method. There is no
                // second pass over this string — no tidying, no re-punctuation, no expansion.
                Description = description,
                DurationSeconds = InRange(item?.DurationSeconds, 1, RecipeStepRules.MaxDurationSeconds),
                IngredientIndexes = RemapIndexes(item?.IngredientIndexes, indexMap),
                Temperature = NormaliseTemperature(item?.Temperature),
            });
        }

        return result;
    }

    /// <summary>
    /// Translates the model's positions through the drop map. Anything that does not land on a
    /// surviving line is DROPPED — never clamped to a neighbour, which would silently attach
    /// the wrong ingredient to a step and be indistinguishable from a deliberate reference.
    /// The result satisfies <c>RecipeStepRules.IngredientIndexesAreValid</c> by construction.
    /// </summary>
    private static List<int> RemapIndexes(List<double?>? raw, int[] indexMap)
    {
        var result = new List<int>();
        if (raw is null)
        {
            return result;
        }

        var seen = new HashSet<int>();
        foreach (var value in raw)
        {
            if (InRange(value, 0, int.MaxValue) is not int index || index >= indexMap.Length)
            {
                continue;
            }

            var mapped = indexMap[index];
            if (mapped >= 0 && seen.Add(mapped))
            {
                result.Add(mapped);
            }
        }

        result.Sort();
        return result;
    }

    private static StepTemperature? NormaliseTemperature(RawTemperature? raw)
    {
        if (raw is null || !Vocabulary.TryParseMember<TemperatureUnit>(raw.Unit, out var unit))
        {
            return null;
        }

        if (InRange(raw.Value, int.MinValue, int.MaxValue) is not int degrees)
        {
            return null;
        }

        var temperature = new StepTemperature { Value = degrees, Unit = unit };
        // Out of the plausible range for its scale means a misreading, not an unusual oven.
        return RecipeStepRules.TemperatureIsValid(temperature) ? temperature : null;
    }

    private static List<RecipeTag> NormaliseTags(List<string?>? raw)
    {
        var result = new List<RecipeTag>();
        if (raw is null)
        {
            return result;
        }

        var seen = new HashSet<RecipeTag>();
        foreach (var item in raw)
        {
            if (result.Count >= MaxTags)
            {
                break;
            }

            if (Vocabulary.TryParseMember<RecipeTag>(item, out var tag) && seen.Add(tag))
            {
                result.Add(tag);
            }
        }

        return result;
    }

    private static string? Clip(string? value, int maxLength)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return null;
        }

        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength].TrimEnd();
    }

    /// <summary>
    /// Rounds and range-checks, returning null when the value falls OUTSIDE the range.
    ///
    /// This is the method that makes the class header's claim concrete, and it is the one place
    /// the difference from the generator is a single word: the generator CLAMPS out-of-range
    /// values into range, this one drops them. A source page stating 900 servings was misread,
    /// and 100 servings is not a better reading of it — it is a number nobody wrote, presented
    /// with the same confidence as the ones that were.
    /// </summary>
    private static int? InRange(double? value, int min, int max)
    {
        if (value is not double v || double.IsNaN(v) || double.IsInfinity(v))
        {
            return null;
        }

        var rounded = Math.Round(v, MidpointRounding.AwayFromZero);
        return rounded < min || rounded > max ? null : (int)rounded;
    }

    private static int? InRange(decimal? value, int min, int max) =>
        value is null ? null : InRange((double)value.Value, min, max);

    // The untrusted boundary. Every field nullable and loosely typed on purpose.
    private sealed record RawIngredient(string? Name, decimal? Quantity, string? Unit);

    private sealed record RawTemperature(double? Value, string? Unit);

    private sealed record RawStep(
        string? Description,
        double? DurationSeconds,
        List<double?>? IngredientIndexes,
        RawTemperature? Temperature);

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
