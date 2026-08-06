using System.Text;
using System.Text.Json;
using RecipeApp.Application.Chat.Abstractions;
using RecipeApp.Application.Recipes.Abstractions;
using RecipeApp.Application.Recipes.Validators;
using RecipeApp.Domain.Entities;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.Services;
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
    private const int MaxIngredients = 60;
    private const int MaxIngredientNameLength = 100;
    private const decimal MaxQuantity = 100_000m;
    private const int MaxSteps = 40;
    private const int MaxStepDescriptionLength = 1000;
    private const int MaxTags = 10;

    // Stream J. Taken FROM the validator's own rules rather than restated here: the duration
    // bound and the per-unit temperature ranges are the same numbers the human write path
    // enforces, and this file's contract is that a generated row is always a row a human
    // could have typed. A restated copy is a copy that can drift.
    private const int MaxDurationSeconds = RecipeStepRules.MaxDurationSeconds;

    // MaxCuisineLength, MaxUnitLength and MaxTagLength went with stream G's typing: a closed
    // vocabulary has no length to bound. Their replacement is membership — a value that is
    // not a member is dropped, which is a stronger guarantee than "at most 60 characters of
    // anything".

    // The human write path requires Quantity > 0 and a valid Unit, so a generated ingredient
    // that has neither cannot be stored as-is. Dropping the ingredient would silently corrupt
    // the recipe (a missing "salt" is worse than an approximate one), so the two get a neutral
    // default instead. Piece is the count-dimension neutral: it asserts nothing about mass or
    // volume, so an ingredient that lands on it is simply never summed with anything, which is
    // the honest outcome when the model failed to say how much.
    private const decimal FallbackQuantity = 1m;
    private const UnitOfMeasure FallbackUnit = UnitOfMeasure.Piece;

    // ── The vocabularies, as the schema and the prompt need them ────────────────────────
    // Enumerated from the enums themselves rather than written out, so a member appended to
    // UnitOfMeasure/Cuisine/RecipeTag reaches the model on the next call with no edit here.
    // That is the property that keeps the prompt honest as the vocabulary grows.
    private static readonly string[] UnitNames = Enum.GetNames<UnitOfMeasure>();
    private static readonly string[] CuisineNames = Enum.GetNames<Cuisine>();
    private static readonly string[] TagNames = Enum.GetNames<RecipeTag>();
    private static readonly string[] TemperatureUnitNames = Enum.GetNames<TemperatureUnit>();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // ── 1. The response schema ──────────────────────────────────────────────────────────
    // Same Google-flavoured OpenAPI dialect as GeminiMessageCaller's default schema
    // (UPPERCASE type enums, no additionalProperties). Everything scalar arrives as STRING
    // or NUMBER and is re-validated below — the schema buys shape, not meaning, and a
    // looser schema costs nothing because the normalisers are the real guard.
    //
    // STREAM G tightens exactly three fields with an `enum` constraint: unit, cuisineType
    // and tags. This is the one place where the schema can buy MEANING and not just shape,
    // because those three now have a closed set of legal values and the dialect can express
    // it. Constraining them at the decoder is worth more than any amount of prompt wording —
    // "cups" cannot come back at all. The normalisers below still re-validate every one of
    // them: the schema is the provider's promise, not ours, and stream E's whole trust
    // boundary rests on never treating a model response as pre-validated.
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
                    required = new[] { "name", "quantity", "unit" },
                },
            },
            // STREAM J types the step. Two of the three new fields are the kind the schema
            // can only shape, not mean (a duration, a temperature value); the third —
            // ingredientIndexes — is the one the schema cannot police at all, because
            // "in range" is a claim about a SIBLING array the dialect has no way to
            // reference. NormaliseSteps is where that is enforced, and it has to be: the
            // indexes are relative to a list that normalisation itself can shorten.
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
        AiPreferenceContext preferences,
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
            BuildSystemPrompt(preferences),
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
    private static string BuildSystemPrompt(AiPreferenceContext preferences)
    {
        var dietaryRestrictions = preferences.DietaryRestrictions;
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
        // Stream G: the three closed vocabularies are spelled out in full. The schema already
        // constrains the decoder to these, so the prompt is not what enforces them — what it
        // buys is a model that PLANS around the vocabulary instead of writing a recipe and
        // then having a value forced into a nearby member. "Omit rather than approximate" is
        // stated for the same reason: a wrong Cuisine is worse than a null one, because the
        // cuisine filter is how people find dishes.
        sb.AppendLine($"- cuisineType: EXACTLY one of [{string.Join(", ", CuisineNames)}]. Omit it entirely when the dish belongs to no particular cuisine — do not force a near match, and use Other only for a real cuisine missing from that list.");
        sb.AppendLine("- caloriesPerServing: your best estimate per serving, or omit it when you cannot estimate honestly. Never guess wildly.");
        sb.AppendLine($"- ingredients: every ingredient needed, each with a name, a quantity GREATER THAN ZERO, and a unit that is EXACTLY one of [{string.Join(", ", UnitNames)}]. Quantities must match the servings figure. Do not put the quantity or the unit inside the name. Prefer Gram and Millilitre for anything weighed or poured; use Piece, Clove, Slice, Can, Package or Bunch for whole items; use Pinch, Dash, Splash, Handful or ToTaste only where a real cook would not measure.");
        // Stream J. The step is a typed object now, and the three additions are worded to
        // resist the three ways a model gets this wrong: it numbers steps that are already
        // numbered; it lists every ingredient on every step; and it repeats the quantity in
        // the prose, which is what makes serving scaling go stale (D17). The last line is
        // the one that matters most — the reference exists precisely so the prose does not
        // have to carry the amount.
        sb.AppendLine("- steps: the method in order, one instruction per step. Do not number them yourself.");
        sb.AppendLine($"- steps[].durationSeconds: how long the step takes, in whole seconds, at most {MaxDurationSeconds}. Include it for anything with a real elapsed time — chopping, simmering, resting, proving. Omit it for an instant step such as \"season to taste\".");
        sb.AppendLine("- steps[].ingredientIndexes: the 0-based positions, in the ingredients array you just wrote, of ONLY the ingredients this step actually uses. A step that uses none (\"preheat the oven\") omits it. Never list the same position twice, and never list every ingredient on every step.");
        sb.AppendLine($"- steps[].temperature: an object {{ value, unit }} where unit is EXACTLY one of [{string.Join(", ", TemperatureUnitNames)}], on steps carried out at a temperature that matters — an oven setting, a frying temperature, a chilling target. Omit it entirely otherwise; do not guess one for a step done on an ordinary hob.");
        sb.AppendLine("- Write the step prose WITHOUT repeating the quantity: say \"stir in the butter\", not \"stir in 2 tbsp butter\". The ingredient reference carries the amount, and the app shows it beside the instruction.");
        sb.AppendLine($"- tags: up to {MaxTags}, each EXACTLY one of [{string.Join(", ", TagNames)}]. Pick only the ones that are genuinely true of the dish; an empty list is fine. Never invent a tag outside that list.");
        sb.AppendLine();
        sb.AppendLine("If the request is not about food at all, still return a sensible recipe that is as close to the request as a cooking app can honestly get.");

        if (dietaryRestrictions.Count > 0)
        {
            sb.AppendLine();
            sb.Append("The user's dietary restrictions are absolute — every ingredient must comply: ");
            sb.AppendLine(string.Join(", ", dietaryRestrictions));
        }

        // Onboarding (stream K) — the generator's DEFAULT, which is all a preference is
        // allowed to be here. The contrast with the block above is deliberate and is spelled
        // out to the model: that one says "absolute", this one says "only when the request
        // leaves it open". A generator that answered "a birthday cake for my daughter" with a
        // Thai dish because Thai is on this list would be obeying the wrong instruction, and
        // unlike a bad recommendation it would produce a saved row the user has to delete.
        //
        // This does NOT force cuisineType to be set: the omit-rather-than-approximate rule
        // above still governs, so a dish that belongs to no cuisine stays null even when the
        // preference nudged what was cooked.
        if (preferences.CuisinePreferences.Count > 0)
        {
            sb.AppendLine();
            sb.Append("When — and only when — the request does not imply a cuisine, a dish, or a set of ingredients of its own, lean toward the cuisines the user usually cooks: ");
            sb.AppendLine(string.Join(", ", preferences.CuisinePreferences));
            sb.AppendLine("Anything the user actually asked for overrides this completely. Never bend a specific request toward one of those cuisines, and never mention this preference in the title or description.");
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

        // Stream J: normalising ingredients can DROP lines (a nameless one carries nothing),
        // which renumbers every line after it — so the steps' 0-based references must be
        // translated through the same pass that did the dropping, not applied to the raw
        // positions the model wrote. That is why NormaliseIngredients hands back a map.
        var (ingredients, ingredientIndexMap) = NormaliseIngredients(raw.Ingredients);
        if (ingredients.Count == 0)
        {
            throw new InvalidOperationException("recipe generator returned a recipe with no usable ingredients.");
        }

        var steps = NormaliseSteps(raw.Steps, ingredientIndexMap);
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
            // Stream G: same IsDefined discipline as difficulty above, but the failure mode
            // differs — an unparseable difficulty falls back to Medium because Recipe.Difficulty
            // is not nullable and every recipe has one, whereas an unparseable cuisine becomes
            // NULL. "No particular cuisine" is a true statement about a dish; "Italian" when
            // the model said "Sicilian" is not.
            NormaliseCuisine(raw.CuisineType),
            // Calories are the one field where a wrong number is worse than no number: they
            // feed the day page's totals and the week's calorie ribbon. A zero or negative
            // estimate is the model declining, so it drops to null instead of clamping up
            // into a fake "1 kcal".
            DropBelowMinimum(raw.CaloriesPerServing, 1, MaxCaloriesPerServing),
            ingredients,
            steps,
            NormaliseTags(raw.Tags));
    }

    /// <summary>
    /// Returns the surviving ingredient lines AND a map from the model's own 0-based
    /// position to the position the line ended up at, or -1 where the line was dropped or
    /// fell past the cap. Stream J needs the map because the steps reference lines BY
    /// POSITION (D16) and this method is what moves them.
    /// </summary>
    private static (List<RecipeIngredient> Ingredients, int[] IndexMap) NormaliseIngredients(
        List<RawIngredient?>? raw)
    {
        var result = new List<RecipeIngredient>();
        if (raw is null)
        {
            return (result, []);
        }

        var indexMap = new int[raw.Count];
        Array.Fill(indexMap, -1);

        for (var rawIndex = 0; rawIndex < raw.Count; rawIndex++)
        {
            var item = raw[rawIndex];
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

            var quantity = item?.Quantity is decimal q && q > 0m ? Math.Min(q, MaxQuantity) : FallbackQuantity;

            // Units.TryParse accepts the member name the schema asks for AND the spellings a
            // model reaches for anyway ("cups", "tbsp."). An unrecognised spelling falls back
            // to Piece rather than being approximated — see the FallbackUnit note.
            var unit = Units.TryParse(item?.Unit, out var parsedUnit) ? parsedUnit : FallbackUnit;

            indexMap[rawIndex] = result.Count;
            result.Add(new RecipeIngredient
            {
                Name = name,
                Quantity = quantity,
                Unit = unit,
            });
        }

        return (result, indexMap);
    }

    private static List<RecipeStep> NormaliseSteps(List<RawStep?>? raw, int[] ingredientIndexMap)
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
                // 0 / absent both mean "no meaningful duration here", which is null — the
                // same reasoning as calories, and what the entity's own comment asks for.
                DurationSeconds = DropBelowMinimum(item?.DurationSeconds, 1, MaxDurationSeconds),
                IngredientIndexes = NormaliseIngredientIndexes(item?.IngredientIndexes, ingredientIndexMap),
                Temperature = NormaliseTemperature(item?.Temperature),
            });
        }

        return result;
    }

    /// <summary>
    /// Stream J, decision D16. The model wrote positions into the ingredient array IT
    /// returned; <see cref="NormaliseIngredients"/> may have dropped lines out of that array.
    /// So every reference is translated through the map, and anything that does not land on
    /// a surviving line is DROPPED — never clamped to a neighbour, which would silently
    /// attach the wrong ingredient to a step and read as deliberate.
    ///
    /// The result satisfies RecipeStepRules.IngredientIndexesAreValid by construction:
    /// in range, because the map only ever yields surviving positions, and distinct, because
    /// of the HashSet. That is the property that lets the endpoint store a generated recipe
    /// without running it back through the human path's validator.
    /// </summary>
    private static List<int> NormaliseIngredientIndexes(List<double?>? raw, int[] ingredientIndexMap)
    {
        var result = new List<int>();
        if (raw is null || raw.Count == 0)
        {
            return result;
        }

        var seen = new HashSet<int>();
        foreach (var value in raw)
        {
            var rawIndex = ClampInt(value, 0, int.MaxValue);
            if (rawIndex is not int i || i >= ingredientIndexMap.Length)
            {
                continue;
            }

            var mapped = ingredientIndexMap[i];
            if (mapped >= 0 && seen.Add(mapped))
            {
                result.Add(mapped);
            }
        }

        return result;
    }

    /// <summary>
    /// A temperature the model could not state coherently becomes NULL rather than a clamped
    /// number. Same rule as calories and for the same reason: "this step has no particular
    /// temperature" is a true statement about a lot of cooking, whereas 300 °C standing in
    /// for a returned 900 is a claim nobody made. An unrecognised unit likewise drops the
    /// whole object — a value without a scale is not a temperature.
    /// </summary>
    private static StepTemperature? NormaliseTemperature(RawTemperature? raw)
    {
        if (raw is null || !Vocabulary.TryParseMember<TemperatureUnit>(raw.Unit, out var unit))
        {
            return null;
        }

        var value = ClampInt(raw.Value, int.MinValue, int.MaxValue);
        if (value is not int degrees)
        {
            return null;
        }

        var temperature = new StepTemperature { Value = degrees, Unit = unit };
        return RecipeStepRules.TemperatureIsValid(temperature) ? temperature : null;
    }

    // Stream G: membership replaces clipping. A tag outside the curated vocabulary is
    // DROPPED, not coerced — there is no near-miss rule that could turn "comforting" into
    // Comfort without also turning "spicy-ish" into Spicy on a dish that isn't.
    //
    // The old case-insensitive dedupe is gone with the strings it protected against: two
    // spellings of one tag can no longer exist, so a HashSet<RecipeTag> is the whole story.
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

    private static Cuisine? NormaliseCuisine(string? raw) =>
        Vocabulary.TryParseMember<Cuisine>(raw, out var cuisine) ? cuisine : null;

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
