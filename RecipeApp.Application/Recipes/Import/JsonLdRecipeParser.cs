using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.Services;
using RecipeApp.Domain.ValueObjects;

namespace RecipeApp.Application.Recipes.Import;

/// <summary>
/// Reads a schema.org/Recipe out of a page's JSON-LD, deterministically and for free
/// (stream L, Tier 1's primary path).
///
/// THIS CLASS IS THE REASON IMPORT IS MOSTLY NOT AN AI FEATURE. Essentially every recipe site
/// publishes structured data, because Google's rich results require it — so the common import
/// is a fetch and a JSON parse, with no model call, no token cost, no latency beyond the
/// network, and no possibility of a hallucinated ingredient. The extraction model exists to
/// cover the remainder, not the norm, and every field this parser learns to read is a field
/// the paid lane stops being asked about.
///
/// ── WHAT REAL PAGES ACTUALLY LOOK LIKE ──────────────────────────────────────────────────
/// The specification is permissive and publishers use all of it, so most of this file is
/// shape-tolerance rather than logic. The recipe node may be the document root, an element of
/// a root array, or buried in an <c>@graph</c> alongside the site's Organization and
/// BreadcrumbList nodes. <c>@type</c> may be a string or an array (<c>["Recipe", "NewsArticle"]</c>).
/// Every string-valued field may instead be an array, an object with a <c>url</c> or
/// <c>text</c>, or a string with HTML inside it. Instructions may be one prose blob, a list of
/// strings, a list of HowToStep objects, or HowToSections each wrapping their own steps.
///
/// The rule throughout: READ WHAT IS THERE, INVENT NOTHING. A field that cannot be understood
/// becomes null and the normaliser decides the default, because a parser that guesses is
/// indistinguishable from a parser that works until the day it is wrong.
///
/// Returning null is a normal outcome, not a failure — it is precisely the signal that sends
/// the import to the extraction model.
/// </summary>
public static class JsonLdRecipeParser
{
    private const int MaxSearchDepth = 12;
    private const int MaxSteps = 60;
    private const int MaxIngredients = 100;
    private const int MaxTitleLength = 200;
    private const int MaxDescriptionLength = 2000;
    private const int MaxStepLength = 1000;
    private const int MaxTimeMinutes = 24 * 60;

    private static readonly Regex FirstInteger = new(@"-?\d+", RegexOptions.Compiled);
    private static readonly Regex LooseDuration = new(
        @"(?<value>\d+(?:\.\d+)?)\s*(?<unit>hours?|hrs?|h|minutes?|mins?|m)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Finds and reads the page's Recipe node. Null when the page has no JSON-LD, none of its
    /// blocks parse, no block contains a Recipe, or the Recipe carries neither ingredients nor
    /// steps — in every one of those cases the caller falls back to the model.
    /// </summary>
    public static ImportedRecipeDraft? TryParse(string? html)
    {
        foreach (var block in HtmlText.JsonLdBlocks(html))
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(block);
            }
            catch (JsonException)
            {
                // A malformed block is common (trailing commas, unescaped quotes from a CMS)
                // and is never fatal: try the next one. This is why regex framing is safe here
                // — a mis-framed block lands exactly in this catch.
                continue;
            }

            using (document)
            {
                if (TryFindRecipe(document.RootElement, 0, out var recipe)
                    && ReadRecipe(recipe) is { } draft)
                {
                    return draft;
                }
            }
        }

        return null;
    }

    // ── Locating the node ───────────────────────────────────────────────────────────────

    private static bool TryFindRecipe(JsonElement element, int depth, out JsonElement recipe)
    {
        recipe = default;
        if (depth > MaxSearchDepth)
        {
            return false;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (TryFindRecipe(item, depth + 1, out recipe))
                    {
                        return true;
                    }
                }

                return false;

            case JsonValueKind.Object:
                if (HasType(element, "Recipe"))
                {
                    recipe = element;
                    return true;
                }

                // Recurse into the containers publishers actually use — @graph above all, but
                // also mainEntity/mainEntityOfPage, which WordPress plugins favour. Recursing
                // into EVERY property instead would eventually find a Recipe nested inside an
                // unrelated node (a "related recipes" carousel) and import the wrong one.
                foreach (var name in new[] { "@graph", "mainEntity", "mainEntityOfPage", "itemListElement" })
                {
                    if (element.TryGetProperty(name, out var child)
                        && TryFindRecipe(child, depth + 1, out recipe))
                    {
                        return true;
                    }
                }

                return false;

            default:
                return false;
        }
    }

    private static bool HasType(JsonElement element, string type)
    {
        if (!element.TryGetProperty("@type", out var typeElement))
        {
            return false;
        }

        return typeElement.ValueKind switch
        {
            JsonValueKind.String => string.Equals(typeElement.GetString(), type, StringComparison.OrdinalIgnoreCase),
            JsonValueKind.Array => typeElement.EnumerateArray().Any(t =>
                t.ValueKind == JsonValueKind.String
                && string.Equals(t.GetString(), type, StringComparison.OrdinalIgnoreCase)),
            _ => false,
        };
    }

    // ── Reading it ──────────────────────────────────────────────────────────────────────

    private static ImportedRecipeDraft? ReadRecipe(JsonElement recipe)
    {
        var ingredients = ReadIngredients(recipe);
        var steps = ReadSteps(recipe);

        // A Recipe node with no ingredients AND no steps is a stub — a search-result summary or
        // a card in a listing page — not something to import. Returning null hands it to the
        // model, which reads the page text and often does better with exactly these pages.
        if (ingredients.Count == 0 && steps.Count == 0)
        {
            return null;
        }

        // D16's indexes, inferred from the two lists that were just read. Runs HERE rather than
        // in the normaliser because it needs both halves together and this is where they meet.
        StepIngredientLinker.Link(steps, ingredients);

        var (prep, cook) = ReadTimes(recipe);

        return new ImportedRecipeDraft
        {
            Title = Clip(ReadString(recipe, "name"), MaxTitleLength),
            Description = Clip(ReadString(recipe, "description"), MaxDescriptionLength),
            PrepTimeMinutes = prep,
            CookTimeMinutes = cook,
            Servings = ReadServings(recipe),
            // schema.org has no difficulty field. Null, and the normaliser picks the default —
            // inferring one from step count or total time would be a number we made up.
            Difficulty = null,
            CuisineType = ReadCuisine(recipe),
            CaloriesPerServing = ReadCalories(recipe),
            Ingredients = ingredients,
            Steps = steps,
            Tags = ReadTags(recipe),
            ImageUrl = ReadImage(recipe),
        };
    }

    private static List<RecipeIngredient> ReadIngredients(JsonElement recipe)
    {
        var result = new List<RecipeIngredient>();

        // "ingredients" is the pre-2017 spelling and is still emitted by older plugins.
        foreach (var name in new[] { "recipeIngredient", "ingredients" })
        {
            if (!recipe.TryGetProperty(name, out var element))
            {
                continue;
            }

            foreach (var line in EnumerateStrings(element))
            {
                if (result.Count >= MaxIngredients)
                {
                    break;
                }

                if (IngredientLineParser.Parse(HtmlText.PlainText(line)) is { } ingredient)
                {
                    result.Add(ingredient);
                }
            }

            if (result.Count > 0)
            {
                break;
            }
        }

        return result;
    }

    private static List<RecipeStep> ReadSteps(JsonElement recipe)
    {
        var texts = new List<string>();
        if (recipe.TryGetProperty("recipeInstructions", out var instructions))
        {
            CollectStepTexts(instructions, texts, 0);
        }

        var result = new List<RecipeStep>();
        foreach (var text in texts)
        {
            if (result.Count >= MaxSteps)
            {
                break;
            }

            var description = Clip(text, MaxStepLength);
            if (string.IsNullOrWhiteSpace(description))
            {
                continue;
            }

            result.Add(new RecipeStep
            {
                // Renumbered from position, ignoring any "position" the source declared —
                // the same discipline the generator applies, so a source that numbers its
                // steps and one that does not produce identical rows.
                StepNumber = result.Count + 1,
                Description = description,
                // D15: the prose is stored exactly as parsed. These two are read OUT of it and
                // added beside it; nothing is removed from the sentence.
                DurationSeconds = StepDetailExtractor.ExtractDurationSeconds(description),
                Temperature = StepDetailExtractor.ExtractTemperature(description),
                // Filled by StepIngredientLinker once the ingredient list is known.
                IngredientIndexes = [],
            });
        }

        return result;
    }

    private static void CollectStepTexts(JsonElement element, List<string> into, int depth)
    {
        if (depth > MaxSearchDepth || into.Count >= MaxSteps)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                // One prose blob for the whole method — the oldest and messiest shape. Split on
                // the line breaks the markup implied; if there are none it stays one step,
                // which is honest: the source really did write it as one paragraph.
                foreach (var line in SplitBlob(element.GetString()))
                {
                    into.Add(line);
                }

                return;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectStepTexts(item, into, depth + 1);
                }

                return;

            case JsonValueKind.Object:
                // A HowToSection ("For the sauce") wraps its own steps. Recurse into the
                // section's list rather than taking its name as an instruction — the heading is
                // not a thing anybody does.
                if (element.TryGetProperty("itemListElement", out var nested))
                {
                    CollectStepTexts(nested, into, depth + 1);
                    return;
                }

                // A HowToStep. "text" is the instruction; "name" is a short label that is often
                // just the first words of it, so it is only used when there is no text at all.
                var text = ReadString(element, "text") ?? ReadString(element, "name");
                if (!string.IsNullOrWhiteSpace(text))
                {
                    foreach (var line in SplitBlob(text))
                    {
                        into.Add(line);
                    }
                }

                return;
        }
    }

    private static IEnumerable<string> SplitBlob(string? raw)
    {
        var text = HtmlText.PlainText(BlockTagsToNewlines(raw));
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            // Drop a bare list marker or leftover numbering that survived the strip.
            if (trimmed.Length > 1)
            {
                yield return trimmed;
            }
        }
    }

    // Applied BEFORE tags are stripped, so a <li>-separated method keeps its boundaries.
    private static string BlockTagsToNewlines(string? html) =>
        string.IsNullOrEmpty(html)
            ? string.Empty
            : Regex.Replace(html, @"</?(p|br|li|ul|ol|div|h[1-6])\b[^>]*>", "\n", RegexOptions.IgnoreCase);

    private static (int? Prep, int? Cook) ReadTimes(JsonElement recipe)
    {
        var prep = ReadDurationMinutes(recipe, "prepTime");
        var cook = ReadDurationMinutes(recipe, "cookTime");
        var total = ReadDurationMinutes(recipe, "totalTime");

        if (prep is not null || cook is not null)
        {
            // One half plus a total gives the other for free, and the arithmetic is the
            // source's own rather than an estimate.
            if (total is int t)
            {
                prep ??= Math.Max(0, t - (cook ?? 0));
                cook ??= Math.Max(0, t - (prep ?? 0));
            }

            return (prep, cook);
        }

        // Only a total. It goes to COOK rather than being split, because Recipe.TotalTimeMinutes
        // is computed as prep + cook and the total is the number the source actually stated —
        // so this keeps the one figure that was published correct. Splitting it in half would
        // invent two figures to preserve the same sum.
        return total is null ? (null, null) : (0, total);
    }

    private static int? ReadDurationMinutes(JsonElement recipe, string property)
    {
        var raw = ReadString(recipe, property);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // The specified form: an ISO 8601 duration, "PT1H30M".
        if (raw.StartsWith('P') || raw.StartsWith("-P"))
        {
            try
            {
                var span = XmlConvert.ToTimeSpan(raw);
                var minutes = (int)Math.Round(span.TotalMinutes, MidpointRounding.AwayFromZero);
                return minutes is >= 0 and <= MaxTimeMinutes ? minutes : null;
            }
            catch (FormatException)
            {
                // Falls through to the loose reader below — plenty of sites emit "PT" prefixes
                // on values that are not valid durations.
            }
            catch (OverflowException)
            {
                return null;
            }
        }

        // The unspecified-but-common form: "30 minutes", "1 hr 15 min".
        var total = 0;
        foreach (Match match in LooseDuration.Matches(raw))
        {
            if (!double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }

            var unit = match.Groups["unit"].Value.ToLowerInvariant();
            total += (int)Math.Round(value * (unit.StartsWith('h') ? 60 : 1), MidpointRounding.AwayFromZero);
        }

        return total is > 0 and <= MaxTimeMinutes ? total : null;
    }

    private static int? ReadServings(JsonElement recipe)
    {
        foreach (var name in new[] { "recipeYield", "yield" })
        {
            if (!recipe.TryGetProperty(name, out var element))
            {
                continue;
            }

            foreach (var candidate in EnumerateStrings(element))
            {
                // "4", "4 servings", "Serves 4", "4-6 people" — the first integer is the
                // serving count in all of them. A range takes its low end, matching how
                // ingredient quantities treat ranges.
                var match = FirstInteger.Match(candidate ?? string.Empty);
                if (match.Success
                    && int.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var servings)
                    && servings is > 0 and <= 100)
                {
                    return servings;
                }
            }
        }

        return null;
    }

    private static Cuisine? ReadCuisine(JsonElement recipe)
    {
        if (!recipe.TryGetProperty("recipeCuisine", out var element))
        {
            return null;
        }

        foreach (var candidate in EnumerateStrings(element))
        {
            // Membership only — the same rule stream G applied to the generator. A cuisine the
            // vocabulary does not have becomes null rather than the nearest member, because the
            // cuisine filter is how people find dishes and a wrong one is worse than none.
            if (Vocabulary.TryParseMember<Cuisine>(HtmlText.PlainText(candidate), out var cuisine))
            {
                return cuisine;
            }
        }

        return null;
    }

    private static List<RecipeTag> ReadTags(JsonElement recipe)
    {
        var result = new List<RecipeTag>();
        var seen = new HashSet<RecipeTag>();

        foreach (var name in new[] { "keywords", "recipeCategory", "suitableForDiet" })
        {
            if (!recipe.TryGetProperty(name, out var element))
            {
                continue;
            }

            foreach (var candidate in EnumerateStrings(element))
            {
                // keywords is frequently one comma-separated string rather than an array.
                foreach (var piece in (HtmlText.PlainText(candidate) ?? string.Empty).Split(','))
                {
                    if (result.Count >= 10)
                    {
                        return result;
                    }

                    if (Vocabulary.TryParseMember<RecipeTag>(piece.Trim(), out var tag) && seen.Add(tag))
                    {
                        result.Add(tag);
                    }
                }
            }
        }

        return result;
    }

    private static int? ReadCalories(JsonElement recipe)
    {
        if (!recipe.TryGetProperty("nutrition", out var nutrition) || nutrition.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var raw = ReadString(nutrition, "calories");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // "250 calories", "250 kcal", "250".
        var match = FirstInteger.Match(raw);
        return match.Success
            && int.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var calories)
            && calories is > 0 and <= 20_000
                ? calories
                : null;
    }

    private static string? ReadImage(JsonElement recipe)
    {
        if (!recipe.TryGetProperty("image", out var element))
        {
            return null;
        }

        return ReadUrlish(element, 0);
    }

    private static string? ReadUrlish(JsonElement element, int depth)
    {
        if (depth > MaxSearchDepth)
        {
            return null;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var value = element.GetString()?.Trim();
                return string.IsNullOrWhiteSpace(value) ? null : value;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (ReadUrlish(item, depth + 1) is { } found)
                    {
                        return found;
                    }
                }

                return null;

            case JsonValueKind.Object:
                // An ImageObject: { "@type": "ImageObject", "url": "..." }. "contentUrl" is the
                // other spelling the specification allows.
                foreach (var name in new[] { "url", "contentUrl" })
                {
                    if (element.TryGetProperty(name, out var url)
                        && ReadUrlish(url, depth + 1) is { } found)
                    {
                        return found;
                    }
                }

                return null;

            default:
                return null;
        }
    }

    // ── Shape tolerance ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// A property that should be a string but may be a number, an array of either, or an
    /// object carrying the value under "text"/"name"/"value". Yields every string it can find
    /// so the caller can take the first usable one.
    /// </summary>
    private static IEnumerable<string?> EnumerateStrings(JsonElement element, int depth = 0)
    {
        if (depth > MaxSearchDepth)
        {
            yield break;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                yield return element.GetString();
                break;

            case JsonValueKind.Number:
                yield return element.GetRawText();
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var value in EnumerateStrings(item, depth + 1))
                    {
                        yield return value;
                    }
                }

                break;

            case JsonValueKind.Object:
                foreach (var name in new[] { "text", "name", "value" })
                {
                    if (element.TryGetProperty(name, out var child))
                    {
                        foreach (var value in EnumerateStrings(child, depth + 1))
                        {
                            yield return value;
                        }

                        break;
                    }
                }

                break;
        }
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
            ? EnumerateStrings(value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))
            : null;

    private static string? Clip(string? value, int maxLength)
    {
        var text = HtmlText.PlainText(value);
        if (text.Length == 0)
        {
            return null;
        }

        return text.Length <= maxLength ? text : text[..maxLength].TrimEnd();
    }
}
