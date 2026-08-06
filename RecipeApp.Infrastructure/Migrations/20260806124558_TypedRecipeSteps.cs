using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RecipeApp.Infrastructure.Migrations
{
    /// <summary>
    /// Stream J. <c>RecipeStep</c> becomes a typed value: <c>TimerSeconds</c> is absorbed by
    /// <c>DurationSeconds</c>, and a step gains <c>IngredientIndexes</c> (decision D16) and an
    /// optional <c>Temperature</c>.
    ///
    /// EF generated nothing, exactly as it did for stream G's vocabularies, and for the same
    /// reason: <c>Recipes.Steps</c> was a jsonb column before this migration and is a jsonb
    /// column after it. The model snapshot has no opinion about what lives INSIDE a jsonb
    /// document, so the schema is unchanged while every stored step's shape is not. Without
    /// the SQL below the deploy would look clean and every recipe written before it would
    /// quietly lose its timer — a null duration reads as "this step has none", which is
    /// indistinguishable from the truth and therefore never gets reported as a bug.
    /// </summary>
    public partial class TypedRecipeSteps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(Up_Sql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(Down_Sql);
        }

        // ═══════════════════════════════════════════════════════════════════════════════
        // Both directions rebuild each step object rather than patching it, so the result
        // does not depend on which keys a given row happened to have. Both are IDEMPOTENT:
        // the coalesce chains fall through to the already-migrated key on a second run.
        //
        // ORDER BY ord inside jsonb_agg is load-bearing. jsonb_array_elements does not
        // promise a stable order without it, and a reordered Steps array would renumber the
        // method against StepNumber — which the detail page sorts by, so the corruption
        // would show up as steps in the wrong order with no error anywhere.
        //
        // The keys are PascalCase because RecipeAppDataSource sets no naming policy: the
        // persisted name is the CLR property name verbatim. Same reason the enum members
        // land as names.
        // ═══════════════════════════════════════════════════════════════════════════════

        private const string Up_Sql = @"
UPDATE ""Recipes""
SET ""Steps"" = (
    SELECT coalesce(jsonb_agg(
        -- TimerSeconds -> DurationSeconds, keeping the value. Absent on a step written by
        -- something that never had the key: null is the honest answer there.
        (step - 'TimerSeconds')
        || jsonb_build_object(
            'DurationSeconds',
            coalesce(step -> 'TimerSeconds', step -> 'DurationSeconds', 'null'::jsonb))
        -- The two genuinely new fields, materialised so the corpus has ONE shape. Reads
        -- would survive their absence (System.Text.Json leaves the property initialiser in
        -- place), but a corpus in two shapes is a corpus where any hand-written jsonb
        -- predicate silently skips half the rows.
        || jsonb_build_object(
            'IngredientIndexes',
            coalesce(step -> 'IngredientIndexes', '[]'::jsonb))
        || jsonb_build_object(
            'Temperature',
            coalesce(step -> 'Temperature', 'null'::jsonb))
        ORDER BY ord), '[]'::jsonb)
    FROM jsonb_array_elements(""Steps"") WITH ORDINALITY AS elements(step, ord))
WHERE jsonb_typeof(""Steps"") = 'array' AND jsonb_array_length(""Steps"") > 0;
";

        // Unlike stream G's normalisation, this one HAS a real inverse — the old shape is a
        // strict subset of the new one, so reverting is a rename plus three dropped keys.
        //
        // What it cannot restore is the information those keys held: a step's ingredient
        // references and its temperature have nowhere to live in a three-field step, so a
        // down-migration discards them. That is the correct outcome (D10 makes the deployed
        // data disposable, and re-running Up cannot invent references nobody typed), but it
        // is a one-way loss and is why this is written out rather than left implicit.
        private const string Down_Sql = @"
UPDATE ""Recipes""
SET ""Steps"" = (
    SELECT coalesce(jsonb_agg(
        (step - 'DurationSeconds' - 'IngredientIndexes' - 'Temperature')
        || jsonb_build_object(
            'TimerSeconds',
            coalesce(step -> 'DurationSeconds', step -> 'TimerSeconds', 'null'::jsonb))
        ORDER BY ord), '[]'::jsonb)
    FROM jsonb_array_elements(""Steps"") WITH ORDINALITY AS elements(step, ord))
WHERE jsonb_typeof(""Steps"") = 'array' AND jsonb_array_length(""Steps"") > 0;
";
    }
}
