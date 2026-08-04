using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using RecipeApp.Application.MealPlanning;
using RecipeApp.Tools.IngredientSeedBuilder;

// ═══════════════════════════════════════════════════════════════════════════════════════
// INGREDIENT SEED BUILDER (stream G, slice G2 — decision D9).
//
// Reads a FoodData Central CSV bulk export and writes the catalogue seed file that
// RecipeApp.Infrastructure embeds. Run BY HAND when the catalogue needs regenerating;
// its output is committed, so nothing in the build, the test run or the deploy ever
// downloads or parses USDA data.
//
//   dotnet run --project tools/IngredientSeedBuilder -- \
//       --usda <dir containing the extracted sr_legacy and foundation CSV folders> \
//       --out  RecipeApp.Infrastructure/Seed/ingredients.seed.json \
//       [--corpus "<postgres connection string>"]
//
// Datasets: SR Legacy + Foundation Foods. Both are US-government works in the public
// domain, which is why D9 chose FDC over Open Food Facts (ODbL share-alike) — and they
// describe GENERIC foods rather than branded products, which is the other half of the
// reason. Provenance is kept per row as FdcId.
//
// --corpus is optional and is the second half of D9: the dataset supplies canonical
// entries, nutrition and densities, but the spellings real users type come from the
// recipes already in the database. See BuildCorpusAliases for what that can and cannot
// do — it is exact-match only, per D8.
// ═══════════════════════════════════════════════════════════════════════════════════════

var options = ParseArgs(args);
if (options is null)
{
    Console.Error.WriteLine("usage: --usda <dir> --out <file> [--corpus <connection string>] [--max <n>]");
    return 1;
}

Console.WriteLine($"Reading FoodData Central from {options.UsdaDirectory}");

var foods = new Dictionary<int, UsdaFood>();
foreach (var dataset in Directory.GetDirectories(options.UsdaDirectory, "*", SearchOption.AllDirectories)
             .Where(d => File.Exists(Path.Combine(d, "food.csv"))))
{
    LoadDataset(dataset, foods);
}

Console.WriteLine($"  {foods.Count:N0} foods after category + brand filtering");

// ── Collapse to one entry per canonical name ────────────────────────────────────────
// Several FDC rows describe the same ingredient at different levels of processing.
// Keeping the most generic (lowest score) is what turns 7,000 analyses into a catalogue.
var byName = new Dictionary<string, UsdaFood>(StringComparer.OrdinalIgnoreCase);
foreach (var food in foods.Values)
{
    var name = Naming.CanonicalName(food.Segments);
    if (name.Length < 3 || name.Length > 60)
    {
        continue;
    }

    if (!byName.TryGetValue(name, out var existing) ||
        Naming.GenericnessScore(food) < Naming.GenericnessScore(existing))
    {
        byName[name] = food;
    }
}

Console.WriteLine($"  {byName.Count:N0} distinct canonical names");

// Trim to the target size: culinary staples first (see Naming.PriorityTerms for why
// genericness alone chooses badly here), then fill the remainder by genericness.
var ranked = byName
    .OrderBy(kv => Naming.IsPriority(kv.Key) ? 0 : 1)
    .ThenBy(kv => Naming.GenericnessScore(kv.Value))
    .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
    .ToList();

var priorityCount = ranked.Count(kv => Naming.IsPriority(kv.Key));
var selected = ranked
    .Take(options.MaxIngredients)
    .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
    .ToList();

Console.WriteLine($"  {selected.Count:N0} kept after the {options.MaxIngredients:N0} cap "
                + $"({Math.Min(priorityCount, options.MaxIngredients):N0} staples, "
                + $"{Math.Max(0, options.MaxIngredients - priorityCount):N0} filled by genericness)");

// ── Ingredients + aliases ───────────────────────────────────────────────────────────
var ingredients = new List<SeedIngredient>(selected.Count);
var aliasOwner = new Dictionary<string, Guid>(StringComparer.Ordinal);

foreach (var (name, food) in selected)
{
    // Deterministic id from the FDC id: re-running the tool must not churn every row,
    // and a recipe that resolved to an ingredient must still resolve after a rebuild.
    var id = DeterministicId(food.FdcId);

    ingredients.Add(new SeedIngredient(
        id,
        name,
        Naming.Categories.GetValueOrDefault(food.CategoryId ?? -1, "Other"),
        Round(food.GramsPerMillilitre, 4),
        Round(food.GramsPerPiece, 2),
        Round(food.Kcal, 1),
        Round(food.ProteinG, 2),
        Round(food.FatG, 2),
        Round(food.CarbsG, 2),
        Round(food.FibreG, 2),
        food.FdcId));

}

// Primary claims FIRST, so the most-typed bare words land where a person means them
// rather than wherever the genericness score happens to point (see Naming.PrimaryClaims).
var byCanonicalName = selected.ToDictionary(
    kv => kv.Key,
    kv => DeterministicId(kv.Value.FdcId),
    StringComparer.OrdinalIgnoreCase);

var claimsMissingTarget = new List<string>();
foreach (var (key, canonical) in Naming.PrimaryClaims)
{
    if (byCanonicalName.TryGetValue(canonical, out var claimed))
    {
        aliasOwner[IngredientKey.For(key)] = claimed;
    }
    else
    {
        claimsMissingTarget.Add($"{key} -> {canonical}");
    }
}

if (claimsMissingTarget.Count > 0)
{
    Console.WriteLine($"  {claimsMissingTarget.Count:N0} primary claims had no target:");
    foreach (var missing in claimsMissingTarget)
    {
        Console.WriteLine($"      {missing}");
    }
}

// Aliases in a SECOND pass, in genericness order rather than the alphabetical order the
// ingredients are emitted in.
//
// MatchKey is the PRIMARY KEY of IngredientAliases, so a key belongs to exactly one
// ingredient and the first writer keeps it. Which writer is first therefore decides what
// a bare head noun means: iterating alphabetically handed "flour" to "00 flour" (an
// Italian pizza flour) purely because zero sorts before every letter. Iterating by
// genericness hands it to a plain wheat flour, which is what someone typing "flour"
// means. The two orders are separated here so neither has to compromise — the seed file
// stays alphabetical and reviewable, the claims stay sensible.
foreach (var (name, food) in selected.OrderBy(kv => Naming.GenericnessScore(kv.Value))
                                     .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
{
    var id = DeterministicId(food.FdcId);
    foreach (var alias in Naming.DatasetAliases(food.Segments, name))
    {
        var key = IngredientKey.For(alias);
        if (key.Length > 0 && key.Length <= 120)
        {
            aliasOwner.TryAdd(key, id);
        }
    }
}

// ── Curated synonyms ────────────────────────────────────────────────────────────────
// Applied AFTER the dataset's own aliases so it can only fill gaps, never take a key
// the data already claimed. A synonym whose target is not in the catalogue is reported
// rather than silently dropped — that means either the cap cut the target or the name
// rules moved it, and both are things to look at.
// The TARGET is resolved through the alias index rather than by exact canonical name.
// Both are exact lookups; the difference is robustness. A canonical name is chosen by
// the naming rules and the cap, so it moves whenever either is tuned — "Wheat flour"
// became "White wheat flour" between two runs of this tool and silently broke four
// synonyms. Every canonical name is also its own alias, so targeting the index costs
// nothing and survives that churn.
var synonymsApplied = 0;
var synonymsMissingTarget = new List<string>();

foreach (var (spelling, target) in Naming.Synonyms)
{
    var targetKey = IngredientKey.For(target);
    if (targetKey.Length == 0 || !aliasOwner.TryGetValue(targetKey, out var id))
    {
        synonymsMissingTarget.Add($"{spelling} -> {target}");
        continue;
    }

    var key = IngredientKey.For(spelling);
    if (key.Length > 0 && aliasOwner.TryAdd(key, id))
    {
        synonymsApplied++;
    }
}

Console.WriteLine($"  {synonymsApplied:N0} curated synonyms applied"
                + (synonymsMissingTarget.Count > 0
                    ? $", {synonymsMissingTarget.Count:N0} had no target in the catalogue"
                    : string.Empty));
foreach (var missing in synonymsMissingTarget)
{
    Console.WriteLine($"      no target: {missing}");
}

// ── Corpus aliases (D9's second half) ───────────────────────────────────────────────
var corpus = new CorpusReport(0, 0, []);
if (options.CorpusConnectionString is not null)
{
    corpus = BuildCorpusAliases(options.CorpusConnectionString, aliasOwner);
    Console.WriteLine($"  corpus: {corpus.DistinctNames:N0} distinct names, {corpus.Resolved:N0} resolve, "
                    + $"{corpus.Unresolved.Count:N0} do not");
}

var seed = new SeedFile(
    GeneratedFrom: "USDA FoodData Central — SR Legacy + Foundation Foods (public domain)",
    Ingredients: ingredients,
    Aliases: aliasOwner
        .OrderBy(kv => kv.Key, StringComparer.Ordinal)
        .Select(kv => new SeedAlias(kv.Key, kv.Value))
        .ToList(),
    UnresolvedCorpusNames: corpus.Unresolved.OrderBy(n => n, StringComparer.Ordinal).ToList());

Directory.CreateDirectory(Path.GetDirectoryName(options.OutputPath)!);
await File.WriteAllTextAsync(options.OutputPath, JsonSerializer.Serialize(seed, new JsonSerializerOptions
{
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
}));

Console.WriteLine($"\nWrote {options.OutputPath}");
Console.WriteLine($"  {ingredients.Count:N0} ingredients, {seed.Aliases.Count:N0} aliases");
Console.WriteLine($"  {ingredients.Count(i => i.Kcal is not null):N0} with calories, "
                + $"{ingredients.Count(i => i.GramsPerMillilitre is not null):N0} with a density, "
                + $"{ingredients.Count(i => i.GramsPerPiece is not null):N0} with a per-piece weight");

Console.WriteLine("\nBy category:");
foreach (var group in ingredients.GroupBy(i => i.Category).OrderByDescending(g => g.Count()))
{
    Console.WriteLine($"  {group.Count(),5:N0}  {group.Key}");
}

// ── The measurement that actually matters ───────────────────────────────────────────
// Row counts say nothing about whether the catalogue WORKS. What works means: a name a
// person would plausibly type resolves. This list is everyday cooking vocabulary, and
// the miss list is the honest to-do — a low score here means the naming rules above
// need work, not that the cap needs raising.
string[] everyday =
[
    "flour", "plain flour", "sugar", "brown sugar", "salt", "black pepper", "olive oil",
    "vegetable oil", "butter", "milk", "eggs", "egg", "water", "garlic", "onion",
    "onions", "red onion", "carrot", "carrots", "celery", "potato", "potatoes",
    "tomato", "tomatoes", "chopped tomatoes", "tomato paste", "chicken breast",
    "chicken thighs", "ground beef", "beef mince", "pork belly", "bacon", "salmon",
    "cod", "prawns", "rice", "basmati rice", "pasta", "spaghetti", "bread",
    "breadcrumbs", "cheddar cheese", "parmesan", "mozzarella", "cream", "sour cream",
    "yogurt", "lemon", "lime", "orange", "apple", "banana", "spinach", "kale",
    "broccoli", "mushrooms", "peppers", "chilli", "ginger", "cumin", "paprika",
    "cinnamon", "oregano", "basil", "parsley", "coriander", "thyme", "rosemary",
    "bay leaf", "soy sauce", "vinegar", "honey", "mustard", "mayonnaise",
    "chickpeas", "lentils", "black beans", "kidney beans", "peas", "sweetcorn",
    "coconut milk", "stock", "almonds", "walnuts", "peanut butter", "oats", "cornflour",
];

var hits = everyday.Where(n => aliasOwner.ContainsKey(IngredientKey.For(n))).ToList();
var misses = everyday.Except(hits).ToList();
Console.WriteLine($"\nEveryday-name coverage: {hits.Count}/{everyday.Length} "
                + $"({100.0 * hits.Count / everyday.Length:N0}%)");
if (misses.Count > 0)
{
    Console.WriteLine($"  misses: {string.Join(", ", misses)}");
}

Console.WriteLine("\nSample (every 60th, for eyeballing before you commit):");
for (var i = 0; i < ingredients.Count; i += 60)
{
    var ing = ingredients[i];
    Console.WriteLine($"  {ing.Name,-42} {ing.Category,-22} {ing.Kcal,6:N0} kcal  fdc {ing.FdcId}");
}

return 0;

// ─────────────────────────────────────────────────────────────────────────────────────

static void LoadDataset(string directory, Dictionary<int, UsdaFood> foods)
{
    var accepted = new HashSet<int>();

    foreach (var row in Csv.Read(Path.Combine(directory, "food.csv")))
    {
        var dataType = row["data_type"];
        // Foundation's export also carries sample_food / market_acquisition rows, which
        // are the physical samples behind an analysis rather than foods in their own right.
        if (dataType is not ("sr_legacy_food" or "foundation_food"))
        {
            continue;
        }

        if (!int.TryParse(row["fdc_id"], out var fdcId))
        {
            continue;
        }

        var categoryId = int.TryParse(row["food_category_id"], out var c) ? c : (int?)null;
        if (categoryId is null || !Naming.Categories.ContainsKey(categoryId.Value))
        {
            continue;
        }

        var description = row["description"].Trim();
        if (description.Length == 0 || Naming.LooksBranded(description))
        {
            continue;
        }

        var segments = description
            .Split(',')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        foods[fdcId] = new UsdaFood
        {
            FdcId = fdcId,
            Description = description,
            DataType = dataType,
            CategoryId = categoryId,
            Segments = segments,
        };
        accepted.Add(fdcId);
    }

    LoadNutrients(Path.Combine(directory, "food_nutrient.csv"), foods, accepted);
    LoadPortions(directory, foods, accepted);
}

static void LoadNutrients(string path, Dictionary<int, UsdaFood> foods, HashSet<int> accepted)
{
    if (!File.Exists(path))
    {
        return;
    }

    // FDC nutrient ids. 1008 is the classic Energy row; 2047/2048 are the newer Atwater
    // calculations that Foundation Foods uses instead, so both are read and 1008 wins.
    foreach (var row in Csv.Read(path))
    {
        if (!int.TryParse(row["fdc_id"], out var fdcId) || !accepted.Contains(fdcId))
        {
            continue;
        }

        if (!foods.TryGetValue(fdcId, out var food) ||
            !double.TryParse(row["amount"], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
        {
            continue;
        }

        switch (row["nutrient_id"])
        {
            case "1008": food.Kcal = amount; break;
            case "2047" or "2048": food.Kcal ??= amount; break;
            case "1003": food.ProteinG = amount; break;
            case "1004": food.FatG = amount; break;
            case "1005": food.CarbsG = amount; break;
            case "1079": food.FibreG = amount; break;
        }
    }
}

static void LoadPortions(string directory, Dictionary<int, UsdaFood> foods, HashSet<int> accepted)
{
    var portionPath = Path.Combine(directory, "food_portion.csv");
    var unitPath = Path.Combine(directory, "measure_unit.csv");
    if (!File.Exists(portionPath) || !File.Exists(unitPath))
    {
        return;
    }

    var unitNames = Csv.Read(unitPath).ToDictionary(r => r["id"], r => r["name"]);

    // SR Legacy sets measure_unit_id to 9999 on EVERY row and writes the unit into the
    // free-text `modifier` instead ("cup", "cup (8 fl oz)", "cup, chopped", "tbsp").
    // Foundation Foods uses real ids. Reading only the ids finds 22 densities across the
    // whole catalogue; reading the modifier too is what makes the density column worth
    // having, which is what G3's cross-dimension summation is built on.
    //
    // The distinction that matters most here is "oz" vs "fl oz": the first is MASS and
    // must never contribute to a density, the second is volume. They differ by two
    // characters and by a factor of the ingredient's own density.

    // Volume measures, in millilitres, using the SAME cooking convention as
    // RecipeApp.Domain.Services.Units — a 240 ml cup, a 15 ml tablespoon. The two must
    // agree or a density derived here would be wrong when applied there.
    var millilitresPerUnit = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
    {
        ["cup"] = 240, ["tablespoon"] = 15, ["teaspoon"] = 5,
        ["liter"] = 1000, ["milliliter"] = 1, ["fl oz"] = 30, ["pint"] = 473,
    };

    // Portion descriptions that mean ONE OF THE THING — the per-piece weight a count
    // unit needs. "1 medium apple" is a piece; "1 slice" is not the whole apple.
    var pieceModifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "medium", "large", "small", "whole", "each", "fruit", "piece", "item",
        "unit", "clove", "head", "bunch", "stalk", "sprig", "leaf", "egg", "fillet",
    };

    // Best (largest-volume) density measurement per food: a cup weighing is a more
    // reliable ratio than a teaspoon weighing, where the scale's error dominates.
    var bestVolume = new Dictionary<int, double>();

    foreach (var row in Csv.Read(portionPath))
    {
        if (!int.TryParse(row["fdc_id"], out var fdcId) || !accepted.Contains(fdcId) ||
            !foods.TryGetValue(fdcId, out var food))
        {
            continue;
        }

        if (!double.TryParse(row["gram_weight"], NumberStyles.Float, CultureInfo.InvariantCulture, out var grams) ||
            grams <= 0)
        {
            continue;
        }

        var amount = double.TryParse(row["amount"], NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && a > 0
            ? a
            : 1;

        var unitName = unitNames.GetValueOrDefault(row["measure_unit_id"], string.Empty);
        var modifier = row["modifier"].Trim();
        var portionDescription = row["portion_description"].Trim();

        // The id first (Foundation), then the modifier text (SR Legacy).
        if (!millilitresPerUnit.TryGetValue(unitName, out var mlPerUnit))
        {
            mlPerUnit = MillilitresFromModifier(modifier);
        }

        if (mlPerUnit > 0)
        {
            var millilitres = mlPerUnit * amount;
            if (millilitres > 0 && (!bestVolume.TryGetValue(fdcId, out var best) || millilitres > best))
            {
                bestVolume[fdcId] = millilitres;
                var density = grams / millilitres;
                // Sanity band: nothing a cook measures is lighter than air-puffed cereal
                // (~0.05) or heavier than honey/salt (~1.6). Outside it, the row is a
                // mis-parse rather than a discovery.
                if (density is >= 0.05 and <= 1.6)
                {
                    food.GramsPerMillilitre = density;
                }
            }
        }
        else if (food.GramsPerPiece is null &&
                 (pieceModifiers.Contains(modifier) || pieceModifiers.Contains(unitName) ||
                  pieceModifiers.Contains(portionDescription) ||
                  // SR Legacy again: the piece word leads the free-text modifier
                  // ("medium", "large (3" dia)", "piece, cooked, excluding refuse").
                  pieceModifiers.Any(p => modifier.StartsWith(p, StringComparison.OrdinalIgnoreCase))))
        {
            var perPiece = grams / amount;
            if (perPiece is >= 1 and <= 5000)
            {
                food.GramsPerPiece = perPiece;
            }
        }
    }
}

/// <summary>
/// Millilitres for one of the volume measures SR Legacy writes into `modifier`, or 0
/// when the modifier is not a volume at all.
///
/// Matching is on the LEADING word(s), so "cup, chopped", "cup (8 fl oz)" and "cup
/// slices" all count as a cup. Those qualified forms are not noise to be discarded:
/// a cook measuring a cup of chopped onion wants the chopped packing density, which is
/// exactly what the row reports.
/// </summary>
static double MillilitresFromModifier(string modifier)
{
    if (modifier.Length == 0)
    {
        return 0;
    }

    var m = modifier.ToLowerInvariant();

    // "fl oz" BEFORE "oz" — the mass ounce must not match the fluid one, and a plain
    // "oz" row must fall through to nothing rather than be read as a volume.
    if (m.StartsWith("fl oz", StringComparison.Ordinal)) return 30;
    if (m.StartsWith("cup", StringComparison.Ordinal)) return 240;
    if (m.StartsWith("tbsp", StringComparison.Ordinal) ||
        m.StartsWith("tablespoon", StringComparison.Ordinal)) return 15;
    if (m.StartsWith("tsp", StringComparison.Ordinal) ||
        m.StartsWith("teaspoon", StringComparison.Ordinal)) return 5;
    if (m.StartsWith("quart", StringComparison.Ordinal)) return 946;
    if (m.StartsWith("pint", StringComparison.Ordinal)) return 473;
    if (m.StartsWith("liter", StringComparison.Ordinal) ||
        m.StartsWith("litre", StringComparison.Ordinal)) return 1000;

    return 0;
}

/// <summary>
/// D9's second half. Every distinct ingredient name already written on a recipe is
/// keyed with the REAL <see cref="IngredientKey"/> and looked up in the alias table the
/// dataset produced.
///
/// What this can do: confirm coverage, and report the misses. What it deliberately does
/// NOT do: invent a mapping for a name that misses. D8's resolver is exact-match only,
/// and a tool that guessed here would be edit-distance matching moved one step earlier —
/// the same wrong merge, just committed to a file instead of computed at runtime. The
/// unresolved list is written into the seed file so the gap is visible and a human can
/// close it deliberately.
/// </summary>
static CorpusReport BuildCorpusAliases(string connectionString, Dictionary<string, Guid> aliasOwner)
{
    var names = new List<string>();
    try
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT DISTINCT jsonb_array_elements("Ingredients")->>'Name' AS name
            FROM "Recipes"
            WHERE jsonb_typeof("Ingredients") = 'array'
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
            {
                names.Add(reader.GetString(0));
            }
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  corpus read failed ({ex.Message}) — continuing with dataset aliases only");
        return new CorpusReport(0, 0, []);
    }

    var resolved = 0;
    var unresolved = new List<string>();
    foreach (var name in names)
    {
        var key = IngredientKey.For(name);
        if (key.Length > 0 && aliasOwner.ContainsKey(key))
        {
            resolved++;
        }
        else if (key.Length > 0)
        {
            unresolved.Add(name);
        }
    }

    return new CorpusReport(names.Count, resolved, unresolved.Distinct(StringComparer.Ordinal).ToList());
}

/// <summary>
/// A stable Guid derived from the FDC id, so regenerating the seed keeps every id it
/// had. Without this a rebuild would orphan every RecipeIngredient.IngredientId in the
/// database — the FK would still point at a row that no longer exists.
/// </summary>
static Guid DeterministicId(int fdcId)
{
    // Fixed namespace bytes + the FDC id. Not a real UUIDv5 (no SHA-1 ceremony needed
    // for a namespace this tool owns), just a documented, reproducible mapping.
    Span<byte> bytes = stackalloc byte[16];
    "G2-USDA-INGRED"u8.CopyTo(bytes);
    BitConverter.TryWriteBytes(bytes[12..], fdcId);
    return new Guid(bytes);
}

static double? Round(double? value, int digits) =>
    value is null ? null : Math.Round(value.Value, digits, MidpointRounding.AwayFromZero);

static Options? ParseArgs(string[] args)
{
    string? usda = null, output = null, corpus = null;
    var max = 1500;

    for (var i = 0; i < args.Length - 1; i++)
    {
        switch (args[i])
        {
            case "--usda": usda = args[++i]; break;
            case "--out": output = args[++i]; break;
            case "--corpus": corpus = args[++i]; break;
            case "--max": max = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        }
    }

    return usda is null || output is null ? null : new Options(usda, output, corpus, max);
}

internal sealed record Options(string UsdaDirectory, string OutputPath, string? CorpusConnectionString, int MaxIngredients);

internal sealed record CorpusReport(int DistinctNames, int Resolved, List<string> Unresolved);

internal sealed record SeedIngredient(
    Guid Id,
    string Name,
    string Category,
    double? GramsPerMillilitre,
    double? GramsPerPiece,
    double? Kcal,
    double? ProteinG,
    double? FatG,
    double? CarbsG,
    double? FibreG,
    int FdcId);

internal sealed record SeedAlias(string MatchKey, Guid IngredientId);

internal sealed record SeedFile(
    string GeneratedFrom,
    List<SeedIngredient> Ingredients,
    List<SeedAlias> Aliases,
    List<string> UnresolvedCorpusNames);
