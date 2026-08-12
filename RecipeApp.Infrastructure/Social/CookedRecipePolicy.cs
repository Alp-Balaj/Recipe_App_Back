using System.Linq.Expressions;
using RecipeApp.Domain.Entities.RecipeInteractions;

namespace RecipeApp.Infrastructure.Social;

/// <summary>
/// The ONE definition of "has this person cooked this dish" (KAN-13, ADR-0005).
/// <c>TimesCooked &gt; 0</c> — never the existence of the <see cref="CookedRecipe"/> row.
/// </summary>
/// <remarks>
/// A row and a cook are not the same thing, and the gap between them is not hypothetical: a
/// row can carry a rating and no cook at all. Rating created one at <c>TimesCooked = 0</c>
/// until KAN-7, those rows are still in the database (ADR-0004 keeps them on purpose), and
/// un-cooking every cook of a rated dish reaches the same state today —
/// <c>CookLogService.UncookEntryAsync</c> steps the count down and leaves the rating standing,
/// because un-cooking is not a retraction of an opinion.
/// <para>
/// So the readers that used row existence were answering a question nobody asked. Rating a
/// dish turned on "you cooked this" on the recipe page and the feed card, counted the rater
/// in "N made this", put their face in the maker avatars, and posted "X cooked Y" to their
/// followers' activity strip. Four surfaces, four separate derivations, one wrong answer.
/// </para>
/// <para>
/// The predicate is the same one D8's Cooked filter and KAN-7's write precondition already
/// use, which is the whole point: the write path, the collection and the social surfaces
/// cannot answer this differently if there is only one answer to give. Composing this rather
/// than hand-writing <c>TimesCooked &gt; 0</c> is the same rule
/// <see cref="RecipeApp.Infrastructure.Recipes.RecipeVisibilityPolicy"/> states for
/// visibility, and for the same reason — a divergent copy is how the drift started.
/// </para>
/// <para>
/// What this deliberately does NOT decide: whether the person has an OPINION. Ratings are a
/// separate fact with a separate lifecycle (ADR-0002), so <c>MyRating</c>, <c>RatingCount</c>
/// and <c>AverageRating</c> stay keyed on <c>Rating != null</c> across every row, zero-cook
/// ones included. A rating a real person gave still counts towards a real average.
/// </para>
/// <para>
/// Nor does it consult <c>CookLogs</c>. The aggregate and the log are two separately-writable
/// records of one fact and they can drift (see <c>CookLogService.UncookEntryAsync</c>), so
/// reading both would be a third answer to the question this type exists to make singular.
/// The aggregate is the authority; a drifted row reads as "not cooked" here exactly as it
/// already does in Cooked.
/// </para>
/// <para>
/// Composed onto a navigation collection with <c>.AsQueryable()</c> —
/// <c>r.CookedBy.AsQueryable().Count(CookedRecipePolicy.IsACook())</c> — which is what lets an
/// expression variable reach inside a correlated subquery instead of being inlined by hand at
/// each site. It stays one translated subquery; the integration tests run against real
/// Postgres, so a translation failure here is a test failure, not a production surprise.
/// </para>
/// </remarks>
public static class CookedRecipePolicy
{
    /// <summary>Rows that represent a cook that actually happened.</summary>
    public static Expression<Func<CookedRecipe, bool>> IsACook() => cr => cr.TimesCooked > 0;

    /// <summary>
    /// The same rule narrowed to one person — "has <paramref name="userId"/> cooked this".
    /// A single expression rather than two composed predicates because EF has to see the whole
    /// condition, and because the pair is exactly what a caller-relative flag needs.
    /// </summary>
    public static Expression<Func<CookedRecipe, bool>> CookedBy(Guid userId) =>
        cr => cr.UserId == userId && cr.TimesCooked > 0;
}
