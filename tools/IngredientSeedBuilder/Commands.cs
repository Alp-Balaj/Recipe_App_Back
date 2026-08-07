using Npgsql;
using RecipeApp.Application.MealPlanning;

namespace RecipeApp.Tools.IngredientSeedBuilder;

/// <summary>
/// The two modes that close the alias loop (Fix B), and the reason the tool has verbs at all.
///
/// Neither reads USDA data. The build mode needs the FoodData Central bulk export, which is
/// deliberately not in the repository — so a loop that could only land accepted aliases "on
/// the next full build" would never land them on a machine that has the recipes but not the
/// CSVs. Both of these work from the COMMITTED seed instead, which is the artefact everyone
/// actually has.
/// </summary>
public static class Commands
{
    /// <summary>
    /// propose — every name the corpus writes that the alias table cannot resolve, run past a
    /// canonicaliser, written to the review file with every row undecided.
    /// </summary>
    public static async Task<int> ProposeAsync(string[] args, CancellationToken ct = default)
    {
        var seedPath = Arg(args, "--seed");
        var reviewPath = Arg(args, "--review");
        var corpus = Arg(args, "--corpus");
        var model = Arg(args, "--model") ?? "gemini-flash-latest";
        var apiKey = Arg(args, "--gemini-key") ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY");

        if (seedPath is null || reviewPath is null)
        {
            Console.Error.WriteLine(
                "usage: propose --seed <ingredients.seed.json> --review <AliasProposals.review.json>\n" +
                "               [--corpus <connection string>] [--model <gemini model>] [--gemini-key <key>]\n" +
                "       the key may also come from the GEMINI_API_KEY environment variable.");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.Error.WriteLine("no Gemini key: pass --gemini-key or set GEMINI_API_KEY.");
            return 1;
        }

        var seed = await SeedDocument.ReadAsync(seedPath, ct);
        Console.WriteLine($"Read {seedPath}: {seed.Ingredients.Count:N0} ingredients, {seed.Aliases.Count:N0} aliases");

        // Live corpus when a connection string is given, otherwise the list the last build
        // recorded. The live read is the honest one — the database has moved since the seed
        // was written — but the recorded list keeps the mode usable with no database at all.
        var resolvable = seed.Aliases.Select(a => a.MatchKey).ToHashSet(StringComparer.Ordinal);
        var unresolved = corpus is not null
            ? Corpus.ReadNames(corpus).Where(n => !Resolves(n, resolvable)).ToList()
            : seed.UnresolvedCorpusNames.Where(n => !Resolves(n, resolvable)).ToList();

        // Distinct by KEY, not by spelling: "Plum tomatoes" and "plum tomato" would otherwise
        // become two proposals racing for one alias, and the second would be refused at merge
        // for colliding with the first. One key, one decision.
        var byKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in unresolved.OrderBy(n => n, StringComparer.Ordinal))
        {
            byKey.TryAdd(IngredientKey.For(name), name);
        }

        var names = byKey.Values.OrderBy(n => n, StringComparer.Ordinal).ToList();
        Console.WriteLine($"  {names.Count:N0} distinct unresolved key(s) to propose for");

        if (names.Count == 0)
        {
            Console.WriteLine("Nothing to propose. The review file was not written.");
            return 0;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var proposer = new GeminiProposer(http, apiKey, model);
        var proposals = await proposer.ProposeAsync(names, seed.Ingredients, ct);

        await AliasProposals.WriteAsync(reviewPath, new AliasProposalFile(
            GeneratedFrom: $"{model} over {names.Count} unresolved corpus name(s)",
            HowToUse: AliasProposals.Instructions,
            Proposals: proposals), ct);

        var placed = proposals.Count(p => p.ProposedIngredientId is not null);
        Console.WriteLine($"\nWrote {reviewPath}");
        Console.WriteLine($"  {placed:N0} proposed, {proposals.Count - placed:N0} left unmapped, {proposals.Count:N0} awaiting review");
        foreach (var group in proposals.GroupBy(p => p.Confidence).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"    {group.Count(),4:N0}  {group.Key}");
        }

        Console.WriteLine("\nNothing has been merged. " + AliasProposals.Instructions);
        return 0;
    }

    /// <summary>
    /// merge — the accepted rows, and only those, appended to the seed's alias table.
    /// </summary>
    public static async Task<int> MergeAsync(string[] args, CancellationToken ct = default)
    {
        var seedPath = Arg(args, "--seed");
        var reviewPath = Arg(args, "--review");
        var dryRun = args.Contains("--dry-run");

        if (seedPath is null || reviewPath is null)
        {
            Console.Error.WriteLine(
                "usage: merge --seed <ingredients.seed.json> --review <AliasProposals.review.json> [--dry-run]");
            return 1;
        }

        var seed = await SeedDocument.ReadAsync(seedPath, ct);
        var review = await AliasProposals.ReadAsync(reviewPath, ct);

        var accepted = review.Proposals.Count(p => p.Accepted == true);
        var undecided = review.Proposals.Count(p => p.Accepted is null);
        Console.WriteLine($"{review.Proposals.Count:N0} proposal(s): {accepted:N0} accepted, "
                        + $"{review.Proposals.Count - accepted - undecided:N0} rejected, {undecided:N0} still undecided");

        var report = AliasMerge.Apply(seed, review.Proposals);

        foreach (var rejection in report.Rejected)
        {
            Console.WriteLine($"  refused: \"{rejection.Name}\" [{rejection.MatchKey}] — {rejection.Reason}");
        }

        if (report.Added.Count == 0)
        {
            Console.WriteLine("\nNothing to add. The seed was not written.");
            return 0;
        }

        Console.WriteLine($"\n{report.Added.Count:N0} alias(es) to add:");
        foreach (var key in report.Added)
        {
            Console.WriteLine($"    {key}");
        }

        if (dryRun)
        {
            Console.WriteLine("\n--dry-run: the seed was not written.");
            return 0;
        }

        await SeedDocument.WriteAsync(seedPath, report.Seed, ct);
        Console.WriteLine($"\nWrote {seedPath}: {report.Seed.Aliases.Count:N0} aliases "
                        + $"(was {seed.Aliases.Count:N0}). Review the diff before committing.");
        return 0;
    }

    private static bool Resolves(string name, HashSet<string> resolvable)
    {
        var key = IngredientKey.For(name);
        return key.Length == 0 || resolvable.Contains(key);
    }

    private static string? Arg(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

/// <summary>Reading the spellings real users have typed. Raw SQL, like the build path.</summary>
public static class Corpus
{
    public static List<string> ReadNames(string connectionString)
    {
        var names = new List<string>();
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

        return names;
    }
}
