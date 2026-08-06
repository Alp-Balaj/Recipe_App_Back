using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Application.Recipes.Validators;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;

namespace RecipeApp.Application.Recipes.Import;

/// <summary>
/// Turns an <see cref="ImportedRecipeDraft"/> into the <see cref="CreateRecipeRequest"/> that
/// the ordinary write path already knows how to store (stream L).
///
/// THE POINT OF THIS CLASS IS HOW LITTLE IT DOES. Both import tiers land here, and from here
/// the recipe travels the SAME road a hand-typed one does — the same
/// <c>CreateRecipeRequestValidator</c>, the same <c>IIngredientResolver</c>, the same jsonb
/// columns. So this file is the last place import gets to be special, and every rule it adds
/// is a rule the human path does not have. It therefore adds as few as it can:
///
///   * it fills values that are genuinely ABSENT, because the request type is non-nullable
///     where the draft is nullable and real recipe pages leave fields out;
///   * it forces Visibility to Private (D15);
///   * it re-checks the D16 indexes against the final list.
///
/// It does NOT clamp, coerce or repair values that are present. The generator normalises
/// aggressively because a model returning 900 servings has nobody to hand a 400 to; import
/// does have somebody — the caller — and the validator is already there to tell them. A page
/// stating something absurd should fail loudly as NotARecipe rather than be quietly rounded
/// into something plausible, because the imported recipe is supposed to be THAT page's recipe
/// and a silently-adjusted one is not.
///
/// STEP TEXT IS NEVER TOUCHED (D15). No paraphrase, no tidying, no second pass. Whatever the
/// parser read or the model transcribed is what gets stored.
/// </summary>
public static class ImportedRecipeNormaliser
{
    /// <summary>
    /// Builds the request, or returns null with a reason when the draft is missing something
    /// no default can honestly supply.
    /// </summary>
    /// <param name="imageUrl">
    /// The RE-HOSTED image URL, or null. The draft's own ImageUrl is a foreign address and is
    /// deliberately ignored here — the orchestrator does the re-hosting and passes the result
    /// in, so no path exists by which a remote URL reaches Recipe.ImageUrl.
    /// </param>
    public static CreateRecipeRequest? TryBuild(
        ImportedRecipeDraft draft,
        string? imageUrl,
        out string failureReason)
    {
        failureReason = string.Empty;

        var title = draft.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            // A recipe with no name cannot be shown in a list, found by search, or told apart
            // from another import. There is no default for it — "Untitled recipe" would be a
            // row the user has to fix before it is worth anything.
            failureReason = "The source did not name a recipe.";
            return null;
        }

        var ingredients = draft.Ingredients ?? [];
        if (ingredients.Count == 0)
        {
            failureReason = "No ingredients could be read from the source.";
            return null;
        }

        var steps = Renumber(draft.Steps ?? [], ingredients.Count);
        if (steps.Count == 0)
        {
            failureReason = "No method steps could be read from the source.";
            return null;
        }

        return new CreateRecipeRequest(
            Title: title,
            // The same fallback RecipeGenerationAssistant.Normalise uses, and for the same
            // reason: the validator requires a non-empty description, many recipe pages have
            // none, and the title is the one true sentence already in hand. Inventing a
            // summary would mean describing a dish nobody described.
            Description: string.IsNullOrWhiteSpace(draft.Description) ? title : draft.Description.Trim(),
            PrepTimeMinutes: draft.PrepTimeMinutes ?? 0,
            CookTimeMinutes: draft.CookTimeMinutes ?? 0,
            // A source that states no yield leaves this genuinely unknown, and the request type
            // has no way to say so. 1 is chosen as "the quantities below, exactly as published,
            // are one batch" — which is true of every recipe by construction, whatever number
            // the author had in mind. It is also the safe direction for the serving scaler:
            // scaling UP from a batch multiplies a real published quantity, whereas guessing 4
            // and being wrong silently divides one.
            Servings: draft.Servings ?? 1,
            // Same fallback as the generator. Difficulty is not in schema.org and cannot be
            // inferred from anything the parser saw without inventing a judgement.
            Difficulty: draft.Difficulty ?? DifficultyLevel.Medium,
            CuisineType: draft.CuisineType,
            CaloriesPerServing: draft.CaloriesPerServing,
            ImageUrl: imageUrl,
            // ── DECISION D15 ────────────────────────────────────────────────────────────
            // Hard-coded, not a parameter, and not a fallback to the author's
            // DefaultRecipeVisibility. That column defaults to Public, so deferring to it
            // would publish somebody else's writing into a social feed on the importer's
            // behalf, as the immediate consequence of pasting a link. The owner promotes it
            // deliberately through the edit path once they have read what they imported.
            //
            // Being a constant here rather than a defaulted argument is the point: there is no
            // call site that can pass something else, so no future caller can reintroduce the
            // preference lookup by accident.
            Visibility: RecipeVisibility.Private,
            Ingredients: ingredients,
            Steps: steps,
            Tags: draft.Tags ?? []);
    }

    /// <summary>
    /// Assigns contiguous 1-based numbers from position and drops any ingredient reference that
    /// does not address a real line.
    ///
    /// The index re-check is belt-and-braces: both producers already emit valid indexes —
    /// <see cref="StepIngredientLinker"/> by construction, the extraction assistant by
    /// normalisation. But D16's invariant ("every stored index is in range") is worth enforcing
    /// at the last point before the request is built, because it is the one property of an
    /// imported step that no human reviews and that fails silently — an out-of-range index
    /// renders as a missing chip, not as an error.
    /// </summary>
    private static List<RecipeStep> Renumber(List<RecipeStep> steps, int ingredientCount)
    {
        var result = new List<RecipeStep>(steps.Count);

        foreach (var step in steps)
        {
            if (string.IsNullOrWhiteSpace(step.Description))
            {
                continue;
            }

            var indexes = new List<int>();
            var seen = new HashSet<int>();
            foreach (var index in step.IngredientIndexes ?? [])
            {
                if (index >= 0 && index < ingredientCount && seen.Add(index))
                {
                    indexes.Add(index);
                }
            }

            result.Add(new RecipeStep
            {
                StepNumber = result.Count + 1,
                Description = step.Description,
                DurationSeconds = step.DurationSeconds is int d && d >= 1 && d <= RecipeStepRules.MaxDurationSeconds
                    ? d
                    : null,
                IngredientIndexes = indexes,
                Temperature = RecipeStepRules.TemperatureIsValid(step.Temperature) ? step.Temperature : null,
            });
        }

        return result;
    }
}
