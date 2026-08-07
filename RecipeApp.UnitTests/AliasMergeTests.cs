using RecipeApp.Application.MealPlanning;
using RecipeApp.Tools.IngredientSeedBuilder;

namespace RecipeApp.UnitTests;

// The merge step of the alias-proposal loop (Fix B), and the only part of the seed builder
// worth testing from here: everything else in that tool reads a USDA export nobody has
// checked in, but THIS decides what goes into IngredientAliases — and an alias sets
// IDENTITY, which drives nutrition, dietary conflicts, density collapsing and whether two
// shopping rows merge. It is the exact thing IngredientKey's no-fuzzy-matching prohibition
// exists to protect, so it gets a gate and the gate gets tests.
public class AliasMergeTests
{
    private static readonly Guid Tomato = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Noodle = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Absent = Guid.Parse("99999999-9999-9999-9999-999999999999");

    private static SeedFile Seed() => new(
        GeneratedFrom: "test",
        Ingredients:
        [
            new SeedIngredient(Tomato, "Red tomatoes", "Vegetables", null, null, null, null, null, null, null, 1),
            new SeedIngredient(Noodle, "Chinese noodles", "Grains & pasta", null, null, null, null, null, null, null, 2),
        ],
        Aliases: [new SeedAlias("tomato", Tomato)],
        UnresolvedCorpusNames: ["Plum tomatoes"]);

    private static AliasProposal Proposal(
        string name, Guid? id, bool? accepted, string? matchKey = null) =>
        new(name, matchKey ?? IngredientKey.For(name), "Red tomatoes", id, "high", "because", accepted);

    [Fact]
    public void Only_an_accepted_proposal_is_merged()
    {
        // NO AUTO-ACCEPT is the whole point of the review file. An undecided row (the state
        // the propose mode writes) and a rejected one must both leave the seed alone.
        var report = AliasMerge.Apply(Seed(),
        [
            Proposal("Plum tomatoes", Tomato, accepted: null),
            Proposal("New potatoes", Tomato, accepted: false),
            Proposal("Udon noodles", Noodle, accepted: true),
        ]);

        Assert.Equal(["udon noodle"], report.Added);
        Assert.Equal(2, report.Seed.Aliases.Count);
        Assert.Contains(report.Seed.Aliases, a => a.MatchKey == "udon noodle" && a.IngredientId == Noodle);
    }

    [Fact]
    public void A_key_the_seed_already_owns_is_rejected_rather_than_overwritten()
    {
        // THE COLLISION RULE. MatchKey is the primary key of IngredientAliases, so a key
        // belongs to exactly one ingredient and the seed builder already resolves ownership
        // in genericness order. Silently reassigning an existing alias would re-point every
        // recipe that ever resolved through it — "tomato" moving from red tomatoes to
        // sun-dried ones would rewrite the nutrition of every dish that said "tomatoes".
        var report = AliasMerge.Apply(Seed(), [Proposal("Tomatoes", Noodle, accepted: true)]);

        Assert.Empty(report.Added);
        var rejection = Assert.Single(report.Rejected);
        Assert.Equal("tomato", rejection.MatchKey);
        Assert.Contains("already", rejection.Reason, StringComparison.OrdinalIgnoreCase);

        // And the incumbent still owns it, pointing where it always did.
        var existing = Assert.Single(report.Seed.Aliases, a => a.MatchKey == "tomato");
        Assert.Equal(Tomato, existing.IngredientId);
    }

    [Fact]
    public void Merging_the_same_review_twice_adds_nothing_the_second_time()
    {
        // Falls out of the collision rule, and is what makes the loop safe to re-run: the
        // second pass sees its own first pass as the incumbent.
        var once = AliasMerge.Apply(Seed(), [Proposal("Udon noodles", Noodle, accepted: true)]);
        var twice = AliasMerge.Apply(once.Seed, [Proposal("Udon noodles", Noodle, accepted: true)]);

        Assert.Empty(twice.Added);
        Assert.Equal(once.Seed.Aliases.Count, twice.Seed.Aliases.Count);
    }

    [Fact]
    public void Two_accepted_proposals_claiming_one_key_do_not_both_land()
    {
        // The same collision, arriving inside a single batch rather than against the seed.
        // First writer keeps the key, matching the builder's own TryAdd semantics.
        var report = AliasMerge.Apply(Seed(),
        [
            Proposal("Udon noodles", Noodle, accepted: true),
            Proposal("udon noodle", Tomato, accepted: true),
        ]);

        Assert.Single(report.Added);
        Assert.Single(report.Rejected);
        Assert.Equal(Noodle, Assert.Single(report.Seed.Aliases, a => a.MatchKey == "udon noodle").IngredientId);
    }

    [Fact]
    public void A_proposal_naming_an_ingredient_the_catalogue_does_not_have_is_rejected()
    {
        // A hallucinated id, or one left behind by a catalogue rebuild that dropped the row.
        // IngredientId is not a database-level foreign key — it lives inside jsonb, where
        // Postgres has no referential integrity to offer — so nothing downstream would catch
        // this. It has to be caught here.
        var report = AliasMerge.Apply(Seed(), [Proposal("Plum tomatoes", Absent, accepted: true)]);

        Assert.Empty(report.Added);
        Assert.Contains("catalogue", Assert.Single(report.Rejected).Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_proposal_with_no_ingredient_at_all_is_rejected()
    {
        // How the proposer says "I don't know". Accepting it anyway must not write a null.
        var report = AliasMerge.Apply(Seed(), [Proposal("Gochujang", null, accepted: true)]);

        Assert.Empty(report.Added);
        Assert.Single(report.Rejected);
    }

    [Fact]
    public void A_match_key_that_disagrees_with_its_own_name_is_rejected()
    {
        // The review file is hand-edited, and MatchKey is in it so a human can see what would
        // actually be written. That makes the two fields able to drift — an edit to Name that
        // leaves a stale key behind would write an alias for a spelling nobody reviewed.
        // IngredientKey.For(Name) is the authority; the file merely displays it.
        var report = AliasMerge.Apply(Seed(),
            [Proposal("Plum tomatoes", Tomato, accepted: true, matchKey: "something else")]);

        Assert.Empty(report.Added);
        Assert.Contains("key", Assert.Single(report.Rejected).Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_name_that_keys_to_nothing_is_rejected()
    {
        var report = AliasMerge.Apply(Seed(), [Proposal("   ", Tomato, accepted: true, matchKey: "")]);

        Assert.Empty(report.Added);
        Assert.Single(report.Rejected);
    }

    [Fact]
    public void The_merged_seed_keeps_its_aliases_ordinally_sorted_and_its_catalogue_untouched()
    {
        // The seed file is reviewed as a DIFF, so a stable order is what keeps an added alias
        // legible instead of reshuffling thousands of lines. Merging aliases must also never
        // touch the ingredient rows: their ids are derived from the FDC id precisely so a
        // rebuild does not orphan RecipeIngredient.IngredientId.
        var before = Seed();
        var report = AliasMerge.Apply(before,
        [
            Proposal("Udon noodles", Noodle, accepted: true),
            Proposal("Aubergine", Tomato, accepted: true),
        ]);

        var keys = report.Seed.Aliases.Select(a => a.MatchKey).ToList();
        Assert.Equal(keys.OrderBy(k => k, StringComparer.Ordinal).ToList(), keys);
        Assert.Equal(before.Ingredients, report.Seed.Ingredients);
    }
}
