using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RecipeApp.Infrastructure.Migrations
{
    /// <summary>
    /// Stream G, slice G1. Units, cuisines, tags and dietary restrictions become closed
    /// vocabularies (decision D10). There is no SCHEMA change — see the SQL below for why
    /// this migration exists anyway.
    /// </summary>
    public partial class TypedUnitsAndVocabularies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(Sql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately empty, and deliberately not an exception either.
            //
            // Down() cannot restore what Up() normalised: "cups", "Cups" and "cup" all
            // became Cup, and a dropped tag left no record of itself. There is no inverse
            // to write. Throwing instead would block a rollback of a LATER migration that
            // has nothing to do with this one, which is a worse outcome than a no-op —
            // the schema this migration leaves behind is byte-identical to the one it
            // found, so reverting it is genuinely a no-op at the schema level.
            //
            // Reverting the CODE without the data is what actually needs care, and D10
            // settles it: the deployed data is test-only and safe to wipe.
        }

        private const string Sql = @"
-- ═══════════════════════════════════════════════════════════════════════════════════════
-- Stream G, slice G1 — the DATA half of typing units, cuisines, tags and restrictions.
--
-- EF generates no schema change here, and that is the hazard. Every column involved keeps
-- its Postgres type: CuisineType was text and stays text (the enum persists by name), and
-- the three jsonb columns stay jsonb. What changes is the set of values those columns are
-- allowed to hold — so without this migration the schema would be ""correct"" while every
-- stored row became unreadable, and the first GET /recipes after deploy would 500 on a
-- Npgsql deserialization failure rather than fail anywhere a migration could catch.
--
-- Decision D10 permits wiping the deployed data outright. This normalises instead, which
-- is strictly kinder and no less of a hard cutover: it is a one-shot rewrite, not a
-- dual-read fallback, and nothing in the running application consults these rules.
--
-- The mapping policy matches the generator's normalisers exactly, which is what keeps the
-- two defensible together:
--   * an unrecognised unit becomes Piece (the count-dimension neutral, never summed);
--   * an unrecognised tag is DROPPED, never approximated;
--   * an unrecognised cuisine becomes NULL — ""no particular cuisine"" is true, a wrong
--     cuisine is not.
--
-- Lookup keys are letters-only and lower-cased, so ""one-pot"", ""One Pot"" and ""onepot"" all
-- reach RecipeTag.OnePot, and a value already in its final form re-normalises to itself
-- (this migration is idempotent).
-- ═══════════════════════════════════════════════════════════════════════════════════════

CREATE OR REPLACE FUNCTION pg_temp.g1_norm(value text) RETURNS text AS $$
    SELECT lower(regexp_replace(coalesce(value, ''), '[^a-zA-Z]', '', 'g'));
$$ LANGUAGE sql IMMUTABLE;

CREATE TEMP TABLE g1_units(k text PRIMARY KEY, v text);
INSERT INTO g1_units(k, v) VALUES
            ('bulb', 'Piece'),
            ('bunch', 'Bunch'),
            ('bunches', 'Bunch'),
            ('can', 'Can'),
            ('cans', 'Can'),
            ('clove', 'Clove'),
            ('cloves', 'Clove'),
            ('count', 'Piece'),
            ('cup', 'Cup'),
            ('cups', 'Cup'),
            ('dash', 'Dash'),
            ('dashes', 'Dash'),
            ('floz', 'FluidOunce'),
            ('fluidounce', 'FluidOunce'),
            ('fluidounces', 'FluidOunce'),
            ('g', 'Gram'),
            ('gr', 'Gram'),
            ('gram', 'Gram'),
            ('grams', 'Gram'),
            ('handful', 'Handful'),
            ('handfuls', 'Handful'),
            ('head', 'Piece'),
            ('item', 'Piece'),
            ('items', 'Piece'),
            ('kg', 'Kilogram'),
            ('kilo', 'Kilogram'),
            ('kilogram', 'Kilogram'),
            ('kilograms', 'Kilogram'),
            ('kilos', 'Kilogram'),
            ('l', 'Litre'),
            ('lb', 'Pound'),
            ('lbs', 'Pound'),
            ('liter', 'Litre'),
            ('liters', 'Litre'),
            ('litre', 'Litre'),
            ('litres', 'Litre'),
            ('milliliter', 'Millilitre'),
            ('milliliters', 'Millilitre'),
            ('millilitre', 'Millilitre'),
            ('millilitres', 'Millilitre'),
            ('ml', 'Millilitre'),
            ('ounce', 'Ounce'),
            ('ounces', 'Ounce'),
            ('oz', 'Ounce'),
            ('pack', 'Package'),
            ('package', 'Package'),
            ('packages', 'Package'),
            ('packet', 'Package'),
            ('packets', 'Package'),
            ('packs', 'Package'),
            ('pc', 'Piece'),
            ('pcs', 'Piece'),
            ('piece', 'Piece'),
            ('pieces', 'Piece'),
            ('pinch', 'Pinch'),
            ('pinches', 'Pinch'),
            ('pound', 'Pound'),
            ('pounds', 'Pound'),
            ('slice', 'Slice'),
            ('slices', 'Slice'),
            ('splash', 'Splash'),
            ('splashes', 'Splash'),
            ('sprig', 'Bunch'),
            ('tablespoon', 'Tablespoon'),
            ('tablespoons', 'Tablespoon'),
            ('tblsp', 'Tablespoon'),
            ('tbs', 'Tablespoon'),
            ('tbsp', 'Tablespoon'),
            ('teaspoon', 'Teaspoon'),
            ('teaspoons', 'Teaspoon'),
            ('tin', 'Can'),
            ('tins', 'Can'),
            ('totaste', 'ToTaste'),
            ('tsp', 'Teaspoon'),
            ('unit', 'Piece'),
            ('units', 'Piece'),
            ('whole', 'Piece');

CREATE TEMP TABLE g1_cuisines(k text PRIMARY KEY, v text);
INSERT INTO g1_cuisines(k, v) VALUES
            ('american', 'American'),
            ('british', 'British'),
            ('caribbean', 'Caribbean'),
            ('chinese', 'Chinese'),
            ('easterneuropean', 'EasternEuropean'),
            ('french', 'French'),
            ('german', 'German'),
            ('greek', 'Greek'),
            ('indian', 'Indian'),
            ('italian', 'Italian'),
            ('japanese', 'Japanese'),
            ('korean', 'Korean'),
            ('mediterranean', 'Mediterranean'),
            ('mexican', 'Mexican'),
            ('middleeastern', 'MiddleEastern'),
            ('nordic', 'Nordic'),
            ('northafrican', 'NorthAfrican'),
            ('other', 'Other'),
            ('portuguese', 'Portuguese'),
            ('spanish', 'Spanish'),
            ('thai', 'Thai'),
            ('turkish', 'Turkish'),
            ('vietnamese', 'Vietnamese');

CREATE TEMP TABLE g1_tags(k text PRIMARY KEY, v text);
INSERT INTO g1_tags(k, v) VALUES
            ('appetizer', 'Appetizer'),
            ('baking', 'Baking'),
            ('bread', 'Bread'),
            ('breakfast', 'Breakfast'),
            ('brunch', 'Brunch'),
            ('budget', 'Budget'),
            ('cake', 'Cake'),
            ('comfort', 'Comfort'),
            ('curry', 'Curry'),
            ('dessert', 'Dessert'),
            ('dinner', 'Dinner'),
            ('drink', 'Drink'),
            ('frying', 'Frying'),
            ('grilling', 'Grilling'),
            ('healthy', 'Healthy'),
            ('highprotein', 'HighProtein'),
            ('holiday', 'Holiday'),
            ('kidfriendly', 'KidFriendly'),
            ('leftovers', 'Leftovers'),
            ('lowcalorie', 'LowCalorie'),
            ('lunch', 'Lunch'),
            ('mealprep', 'MealPrep'),
            ('nocook', 'NoCook'),
            ('onepot', 'OnePot'),
            ('partyfood', 'PartyFood'),
            ('pasta', 'Pasta'),
            ('pizza', 'Pizza'),
            ('quick', 'Quick'),
            ('roasting', 'Roasting'),
            ('salad', 'Salad'),
            ('sandwich', 'Sandwich'),
            ('sidedish', 'SideDish'),
            ('slowcooker', 'SlowCooker'),
            ('snack', 'Snack'),
            ('soup', 'Soup'),
            ('spicy', 'Spicy'),
            ('stew', 'Stew'),
            ('vegan', 'Vegan'),
            ('vegetarian', 'Vegetarian');

CREATE TEMP TABLE g1_restrictions(k text PRIMARY KEY, v text);
INSERT INTO g1_restrictions(k, v) VALUES
            ('dairyfree', 'DairyFree'),
            ('eggfree', 'EggFree'),
            ('glutenfree', 'GlutenFree'),
            ('halal', 'Halal'),
            ('kosher', 'Kosher'),
            ('lowcarb', 'LowCarb'),
            ('lowsodium', 'LowSodium'),
            ('nutfree', 'NutFree'),
            ('peanutfree', 'PeanutFree'),
            ('pescatarian', 'Pescatarian'),
            ('shellfishfree', 'ShellfishFree'),
            ('soyfree', 'SoyFree'),
            ('vegan', 'Vegan'),
            ('vegetarian', 'Vegetarian');

-- 1. Ingredient units. WITH ORDINALITY + ORDER BY preserves the author's ingredient order,
--    which jsonb_agg would otherwise be free to disturb.
UPDATE ""Recipes"" r
SET ""Ingredients"" = (
    SELECT coalesce(jsonb_agg(
               jsonb_set(elem, '{Unit}', to_jsonb(coalesce(u.v, 'Piece')))
               ORDER BY ord), '[]'::jsonb)
    FROM jsonb_array_elements(r.""Ingredients"") WITH ORDINALITY AS t(elem, ord)
    LEFT JOIN g1_units u ON u.k = pg_temp.g1_norm(elem->>'Unit')
)
WHERE jsonb_typeof(r.""Ingredients"") = 'array' AND jsonb_array_length(r.""Ingredients"") > 0;

-- 2. Tags. The INNER join is the drop: an unmatched tag contributes no row.
UPDATE ""Recipes"" r
SET ""Tags"" = (
    SELECT coalesce(jsonb_agg(DISTINCT to_jsonb(g.v)), '[]'::jsonb)
    FROM jsonb_array_elements_text(r.""Tags"") AS t(tag)
    JOIN g1_tags g ON g.k = pg_temp.g1_norm(tag)
)
WHERE jsonb_typeof(r.""Tags"") = 'array';

-- jsonb_agg over zero rows is NULL, not '[]' — a recipe whose every tag was dropped would
-- otherwise end up with a NULL column the CLR List<RecipeTag> cannot hold.
UPDATE ""Recipes"" SET ""Tags"" = '[]'::jsonb WHERE ""Tags"" IS NULL;

-- 3. Cuisine. No match -> NULL, deliberately.
UPDATE ""Recipes"" r
SET ""CuisineType"" = (SELECT c.v FROM g1_cuisines c WHERE c.k = pg_temp.g1_norm(r.""CuisineType""))
WHERE r.""CuisineType"" IS NOT NULL;

-- 4. Dietary restrictions. Same drop-the-unknown rule as tags.
UPDATE ""Users"" u
SET ""DietaryRestrictions"" = (
    SELECT coalesce(jsonb_agg(DISTINCT to_jsonb(g.v)), '[]'::jsonb)
    FROM jsonb_array_elements_text(u.""DietaryRestrictions"") AS t(restriction)
    JOIN g1_restrictions g ON g.k = pg_temp.g1_norm(restriction)
)
WHERE jsonb_typeof(u.""DietaryRestrictions"") = 'array';

UPDATE ""Users"" SET ""DietaryRestrictions"" = '[]'::jsonb WHERE ""DietaryRestrictions"" IS NULL;

DROP TABLE g1_units, g1_cuisines, g1_tags, g1_restrictions;
";
    }
}
