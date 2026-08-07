using System.Text;
using System.Text.Json;
using RecipeApp.Application.MealPlanning;
using RecipeApp.Application.Recipes.Abstractions;
using RecipeApp.Application.Scanning.Abstractions;
using RecipeApp.Infrastructure.Chat;

namespace RecipeApp.Infrastructure.Scanning;

// ═══════════════════════════════════════════════════════════════════════════════════════════
// THE SCANNER'S CONSTRUCTION SEAM (stream N), built to the one-file discipline of
// RecipeExtractionAssistant: schema, prompt, parse and re-validation together, because a
// change to any one of them is usually a change to the others.
//
// Same family as the extractor, not the generator: this TRANSCRIBES a photograph, so the
// re-validation DROPS what it cannot accept and never repairs it. The prompt's one job is
// the extractor's one job relocated — stop the model helping. A model shown a fridge shelf
// will, unprompted, add the salt and oil every kitchen "must" have, round "some cheese" up
// to a named cheese it cannot actually see, and read a receipt's loyalty-points line as
// groceries. Every one of those makes the scan read BETTER and makes it a lie about the
// photograph.
// ═══════════════════════════════════════════════════════════════════════════════════════════
public class FoodScanAssistant : IFoodScanAssistant
{
    // A pantry detection is an ingredient name and lands next to ingredient lines capped at
    // 100 chars; a receipt line lands in ShoppingListItem.Ingredient (200) / .Quantity (50),
    // so the caps here are exactly the caps of where each string ends up.
    private const int MaxDetectedNameLength = 100;
    private const int MaxDetections = 50;
    private const int MaxReceiptNameLength = 200;
    private const int MaxReceiptQuantityLength = 50;
    private const int MaxReceiptItems = 100;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // Same Google-flavoured OpenAPI dialect as the other schemas in this codebase. Nothing
    // beyond the list itself is required, and the LIST being required is what lets an empty
    // one mean "no food here" rather than "the model skipped the field".
    private static readonly object PantrySchema = new
    {
        type = "OBJECT",
        properties = new
        {
            ingredients = new { type = "ARRAY", items = new { type = "STRING" } },
        },
        required = new[] { "ingredients" },
    };

    private static readonly object ReceiptSchema = new
    {
        type = "OBJECT",
        properties = new
        {
            items = new
            {
                type = "ARRAY",
                items = new
                {
                    type = "OBJECT",
                    properties = new
                    {
                        name = new { type = "STRING" },
                        quantity = new { type = "STRING" },
                    },
                    required = new[] { "name" },
                },
            },
        },
        required = new[] { "items" },
    };

    private readonly IVisionMessageCaller _visionCaller;

    public FoodScanAssistant(IVisionMessageCaller visionCaller)
    {
        _visionCaller = visionCaller;
    }

    public async Task<PantryDetection> DetectPantryAsync(
        IReadOnlyList<RecipeImageContent> images,
        CancellationToken cancellationToken = default)
    {
        var parts = images.Select(i => new VisionImagePart(i.Content, i.ContentType)).ToList();

        var call = await _visionCaller.CreateJsonMessageFromImagesAsync(
            BuildPantryPrompt(),
            parts,
            "List the food items visible in these photos.",
            PantrySchema,
            cancellationToken: cancellationToken);

        return new PantryDetection(NormaliseDetections(ParsePantry(call.Json).Ingredients), call.Usage);
    }

    public async Task<ReceiptRead> ReadReceiptAsync(
        IReadOnlyList<RecipeImageContent> images,
        CancellationToken cancellationToken = default)
    {
        var parts = images.Select(i => new VisionImagePart(i.Content, i.ContentType)).ToList();

        var call = await _visionCaller.CreateJsonMessageFromImagesAsync(
            BuildReceiptPrompt(),
            parts,
            "Transcribe the purchased grocery lines on this receipt.",
            ReceiptSchema,
            cancellationToken: cancellationToken);

        return new ReceiptRead(NormaliseReceipt(ParseReceipt(call.Json).Items), call.Usage);
    }

    // ── The prompts ─────────────────────────────────────────────────────────────────────
    private static string BuildPantryPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are naming the food items VISIBLE in photographs of someone's kitchen — a fridge shelf, a counter, a cupboard. You are NOT composing a pantry and NOT suggesting recipes.");
        sb.AppendLine();
        sb.AppendLine("THE ONE RULE: name what you can actually see. Do not complete the picture.");
        sb.AppendLine("Specifically, and these are the mistakes that matter:");
        sb.AppendLine("- Never add a staple the kitchen 'must surely' have. No visible salt means no salt in the list.");
        sb.AppendLine("- Never sharpen a guess into a claim. If you can see cheese but not which, say \"cheese\" — do not name a variety you cannot read.");
        sb.AppendLine("- If you genuinely cannot tell what something is, leave it out rather than guessing.");
        sb.AppendLine("- Packaged goods: name the FOOD, not the brand (\"milk\", not a brand name).");
        sb.AppendLine("- One entry per distinct item. Do not list the same thing twice because two packets are visible.");
        sb.AppendLine();
        sb.AppendLine("Each entry is a plain ingredient name as a shopper would say it (\"red onion\", \"cheddar\", \"eggs\").");
        sb.AppendLine($"No quantities, no packaging sizes, no preparation words. At most {MaxDetectedNameLength} characters each.");
        sb.AppendLine();
        sb.AppendLine("If the photographs contain no food at all, return an empty list. An empty list is a correct answer, not a failure.");
        return sb.ToString();
    }

    private static string BuildReceiptPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are transcribing the PURCHASED LINES of a shop receipt photograph. You are NOT writing a shopping list of your own.");
        sb.AppendLine();
        sb.AppendLine("THE ONE RULE: reproduce what the receipt says. Do not improve it.");
        sb.AppendLine("- Include only lines that are purchased grocery items: food, drink, household consumables.");
        sb.AppendLine("- Exclude everything that is not a purchase: totals, subtotals, tax, discounts, payment and change lines, loyalty points, deposits, store information.");
        sb.AppendLine("- name: the item as printed, tidied only in casing (\"Whole milk\", not \"WHL MLK\" only if the receipt itself prints the full words elsewhere — when only an abbreviation is printed, keep the abbreviation rather than guessing at it).");
        sb.AppendLine($"  At most {MaxReceiptNameLength} characters.");
        sb.AppendLine($"- quantity: the amount AS PRINTED (\"2 x 500g\", \"0.746 kg\"), at most {MaxReceiptQuantityLength} characters. It is a note, not a measurement — do not convert, normalise or compute it. If the line shows no amount, omit it.");
        sb.AppendLine("- Keep the receipt's own order. A repeated line is a real repeat — keep both.");
        sb.AppendLine();
        sb.AppendLine("If the photograph is not a receipt, or is illegible, return an empty list. Do not invent purchases.");
        return sb.ToString();
    }

    // ── The parse ───────────────────────────────────────────────────────────────────────
    private static RawPantry ParsePantry(string json)
    {
        RawPantry? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<RawPantry>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("food scanner returned malformed JSON.", ex);
        }

        return parsed ?? throw new InvalidOperationException("food scanner returned nothing.");
    }

    private static RawReceipt ParseReceipt(string json)
    {
        RawReceipt? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<RawReceipt>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("receipt scanner returned malformed JSON.", ex);
        }

        return parsed ?? throw new InvalidOperationException("receipt scanner returned nothing.");
    }

    // ── The re-validation ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Drops empties, clips to the cap, and dedupes on <see cref="IngredientKey"/> — the
    /// SAME key the matcher downstream will use, so "Eggs" and "egg" collapse here rather
    /// than surfacing as two detections that then match identically. First spelling wins.
    /// </summary>
    private static List<string> NormaliseDetections(List<string?>? raw)
    {
        var result = new List<string>();
        if (raw is null)
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in raw)
        {
            if (result.Count >= MaxDetections)
            {
                break;
            }

            var name = Clip(item, MaxDetectedNameLength);
            if (name is null)
            {
                continue;
            }

            var key = IngredientKey.For(name);
            if (key.Length > 0 && seen.Add(key))
            {
                result.Add(name);
            }
        }

        return result;
    }

    // No dedupe here, deliberately: a receipt printing milk twice sold milk twice, and the
    // draft should show the receipt.
    private static List<ReceiptLine> NormaliseReceipt(List<RawReceiptItem?>? raw)
    {
        var result = new List<ReceiptLine>();
        if (raw is null)
        {
            return result;
        }

        foreach (var item in raw)
        {
            if (result.Count >= MaxReceiptItems)
            {
                break;
            }

            var name = Clip(item?.Name, MaxReceiptNameLength);
            if (name is null)
            {
                continue;
            }

            result.Add(new ReceiptLine(name, Clip(item?.Quantity, MaxReceiptQuantityLength)));
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

    // The untrusted boundary. Every field nullable and loosely typed on purpose.
    private sealed record RawPantry(List<string?>? Ingredients);

    private sealed record RawReceiptItem(string? Name, string? Quantity);

    private sealed record RawReceipt(List<RawReceiptItem?>? Items);
}
