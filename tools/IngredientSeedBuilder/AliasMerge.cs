using RecipeApp.Application.MealPlanning;

namespace RecipeApp.Tools.IngredientSeedBuilder;

/// <summary>Why a proposal did not land. Printed, so a rejected row is never silent.</summary>
public sealed record MergeRejection(string Name, string MatchKey, string Reason);

/// <summary>The merged seed, plus what happened to every proposal that was accepted.</summary>
public sealed record MergeReport(SeedFile Seed, List<string> Added, List<MergeRejection> Rejected);

/// <summary>
/// The gate between a reviewed proposal and the alias table.
///
/// An alias sets ingredient IDENTITY: nutrition totals, dietary conflict checks, density
/// collapsing, and whether two shopping-list rows are one. That is the opposite end of the
/// cost scale from the aisle fallback in ShoppingAisles, which is allowed to guess precisely
/// because it can only ever move a heading. So this end takes no guesses at all — every rule
/// below refuses rather than repairs, and the seed comes back unchanged when a row is bad.
/// </summary>
public static class AliasMerge
{
    public static MergeReport Apply(SeedFile seed, IReadOnlyList<AliasProposal> proposals)
    {
        // The incumbent owners, and the set the batch itself is about to add to. Both live in
        // one dictionary so an in-batch collision is refused by exactly the same rule that
        // refuses a collision with the committed seed — there is no second, weaker path.
        var owners = seed.Aliases.ToDictionary(a => a.MatchKey, a => a.IngredientId, StringComparer.Ordinal);
        var catalogue = seed.Ingredients.Select(i => i.Id).ToHashSet();

        var added = new List<string>();
        var rejected = new List<MergeRejection>();

        foreach (var proposal in proposals)
        {
            // No auto-accept. Undecided and rejected rows are not failures and are not
            // reported as such — they are the normal state of a file nobody has read yet.
            if (proposal.Accepted != true)
            {
                continue;
            }

            // The name is the authority and the key is a display of it, so the key is
            // recomputed rather than trusted. A row whose two fields have drifted apart —
            // an edited Name over a stale key — would otherwise write an alias for a
            // spelling no human ever looked at.
            var key = IngredientKey.For(proposal.Name);
            if (key.Length == 0)
            {
                rejected.Add(new MergeRejection(proposal.Name, proposal.MatchKey, "the name keys to nothing"));
                continue;
            }

            if (!string.Equals(key, proposal.MatchKey, StringComparison.Ordinal))
            {
                rejected.Add(new MergeRejection(proposal.Name, proposal.MatchKey,
                    $"the file's key disagrees with the name, which keys to \"{key}\""));
                continue;
            }

            if (proposal.ProposedIngredientId is not Guid id)
            {
                rejected.Add(new MergeRejection(proposal.Name, key, "no ingredient was proposed"));
                continue;
            }

            // IngredientId is not a database foreign key and cannot be — it lives inside a
            // jsonb document, where Postgres has no referential integrity to offer. A
            // hallucinated or stale id would therefore go entirely uncaught downstream.
            if (!catalogue.Contains(id))
            {
                rejected.Add(new MergeRejection(proposal.Name, key,
                    $"{id} is not in the catalogue"));
                continue;
            }

            // THE COLLISION RULE. MatchKey is the primary key of IngredientAliases, so a key
            // belongs to exactly one ingredient, and the seed builder already resolved that
            // ownership in genericness order. Reassigning one here would silently re-point
            // every recipe that ever resolved through it, rewriting the nutrition of dishes
            // nobody touched. Refuse; never overwrite.
            if (owners.TryGetValue(key, out var incumbent))
            {
                rejected.Add(new MergeRejection(proposal.Name, key,
                    incumbent == id
                        ? "already an alias of this ingredient"
                        : $"already an alias of {NameOf(seed, incumbent)}"));
                continue;
            }

            owners[key] = id;
            added.Add(key);
        }

        // Ordinally sorted, like the build path emits it: the seed is reviewed as a diff, and
        // a stable order is what keeps one added alias legible instead of reshuffling
        // thousands of lines. The ingredient rows are handed back untouched.
        var merged = seed with
        {
            Aliases = owners
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new SeedAlias(kv.Key, kv.Value))
                .ToList(),
        };

        return new MergeReport(merged, added, rejected);
    }

    private static string NameOf(SeedFile seed, Guid id) =>
        seed.Ingredients.FirstOrDefault(i => i.Id == id)?.Name ?? id.ToString();
}
