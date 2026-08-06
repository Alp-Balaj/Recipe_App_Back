using RecipeApp.Domain.Enums;

namespace RecipeApp.Domain.ValueObjects;

/// <summary>
/// One instruction of a recipe's method, stored inside the <c>Recipe.Steps</c> jsonb column.
///
/// Until stream J this was three fields — a number, free-text prose and an optional timer —
/// and everything a cook-mode surface needs beyond a step carousel was unavailable: which
/// lines this step consumes, how long it takes, how hot the oven should be. Those three are
/// what J adds, and they are what makes per-step highlighting, serving scaling and grounded
/// substitution arithmetic rather than prompting.
/// </summary>
public class RecipeStep
{
    /// <summary>1-based position in the method. Contiguous and gapless: the write paths
    /// assign it (the form from the field-array index, the generator by renumbering the
    /// steps that survived normalisation) rather than trusting an author or a model.</summary>
    public int StepNumber { get; set; }

    public string Description { get; set; } = null!;

    /// <summary>
    /// How long this step takes, in whole seconds, or null when it has no meaningful
    /// duration ("season to taste").
    ///
    /// This ABSORBS the old <c>TimerSeconds</c> (stream J, hard cutover per D10 — the
    /// migration renames the key in place and nothing dual-reads the old one). The rename is
    /// not cosmetic: <c>TimerSeconds</c> meant "an unattended wait worth setting a kitchen
    /// timer for", which is a strictly narrower claim, and the generator's prompt said so.
    /// A duration is true of far more steps — chopping an onion takes 90 seconds whether or
    /// not anybody times it — and a step total that omits the attended work is a step total
    /// that lies about how long the recipe takes.
    /// </summary>
    public int? DurationSeconds { get; set; }

    /// <summary>
    /// Which of the recipe's own ingredient lines this step consumes, as ZERO-BASED indexes
    /// into <c>Recipe.Ingredients</c>. Empty is the common, legal case: "preheat the oven"
    /// consumes nothing.
    ///
    /// ── DECISION D16 (settled inside stream J, 2026-08-06) ────────────────────────────
    /// An index, NOT the catalogue <c>RecipeIngredient.IngredientId</c>, and not both.
    ///
    /// The catalogue id cannot be the reference because D8 makes it nullable BY DESIGN —
    /// "resolve, don't constrain". A line the catalogue has never heard of saves anyway with
    /// a null id, so a step reading "fold in the gochujang" would have nothing to point at.
    /// A reference shape that cannot address every line is not a reference shape; it is a
    /// reference shape plus a silent hole, and the hole opens on exactly the interesting
    /// ingredients.
    ///
    /// An index is TOTAL: every line has a position, resolved or not. The known objection is
    /// that a position shifts when a line is deleted — which the recipe form permits. That
    /// is a CLIENT problem inside one editing session, not a storage problem, because PUT is
    /// a full replace: the ingredient list and the step list travel in one body and land in
    /// one row together. There is no moment at which a stored index refers to a list it did
    /// not arrive with. Two things close the rest of it:
    ///
    ///   1. Both request validators reject a step whose indexes fall outside the request's
    ///      own ingredient list, or repeat within one step. "Every index is in range" is
    ///      therefore an invariant of every stored recipe rather than a hope about clients.
    ///   2. The form remaps on delete — refs to the removed line are dropped, refs above it
    ///      shift down — so the shift is handled where the shift happens.
    ///
    /// Storing both was rejected as denormalisation with a failure mode: the catalogue id is
    /// already reachable THROUGH the index (<c>Ingredients[i].IngredientId</c>), so a second
    /// copy could only ever agree redundantly or disagree wrongly.
    /// </summary>
    public List<int> IngredientIndexes { get; set; } = [];

    /// <summary>
    /// The temperature this step is carried out at, or null — which is what most steps are.
    /// Null and "no temperature matters here" are the same statement; there is no zero to
    /// confuse it with, which is why this is a nullable object rather than a nullable number
    /// plus a unit that would be meaningless on its own.
    /// </summary>
    public StepTemperature? Temperature { get; set; }
}

/// <summary>
/// A temperature and the unit it was written in, stored inside a <see cref="RecipeStep"/>.
///
/// The unit is KEPT rather than normalised to one canonical scale on write. An author who
/// typed 350 °F wrote a number a reader recognises; storing 177 °C and rendering it back
/// turns a familiar oven setting into a suspicious one. Conversion is arithmetic and belongs
/// at the point of display, where the reader's preference is known.
/// </summary>
public class StepTemperature
{
    /// <summary>Whole degrees. Oven settings, frying temperatures and chilling targets are
    /// all written as whole numbers, and a fractional oven temperature claims a precision no
    /// domestic oven has.</summary>
    public int Value { get; set; }

    /// <summary>Serialized by NAME inside the jsonb — see RecipeAppDataSource. This is the
    /// first enum stream J puts inside the Steps column, and JsonbEnumEncodingTests asserts
    /// on the raw text because a round-trip cannot tell the two encodings apart.</summary>
    public TemperatureUnit Unit { get; set; }
}
