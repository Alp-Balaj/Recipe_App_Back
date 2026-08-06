using System.Globalization;
using System.Text.RegularExpressions;
using RecipeApp.Application.Recipes.Validators;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;

namespace RecipeApp.Application.Recipes.Import;

/// <summary>
/// Recovers the two typed halves of a step — how long it takes, how hot it is — from the prose
/// the source already wrote (stream L, feeding stream J's step shape).
///
/// This reads the step text and NEVER rewrites it (D15: steps are stored as parsed). "Bake at
/// 180°C for 25 minutes" keeps every word AND gains DurationSeconds=1500 and 180 °C. That
/// redundancy is intentional: the prose is what the source said and must survive verbatim, the
/// typed fields are what cook mode's timers and the step-total arithmetic read. Stripping the
/// duration out of the sentence once it had been captured would have looked tidier and would
/// have made the recipe worse to read aloud.
///
/// TWO THINGS IT DELIBERATELY REFUSES TO GUESS.
///
///   * A BARE DEGREE SIGN. "Heat to 180°" gets no temperature at all, because a value without
///     a scale is not a temperature — the same rule RecipeGenerationAssistant.NormaliseTemperature
///     applies to the model. The tempting heuristic ("≥250 means Fahrenheit") is right often
///     enough to be dangerous: it silently converts a recipe into one whose oven setting nobody
///     wrote, and an oven is the one place a wrong number burns dinner.
///   * GAS MARK. A real UK oven scale with no member in TemperatureUnit. Mapping gas mark 4 to
///     180 °C would be inventing a precision the source did not have; it stays in the prose,
///     where a British cook reads it correctly and the app makes no claim about it.
/// </summary>
public static class StepDetailExtractor
{
    // Ranges take the LOW end, matching IngredientLineParser: "25-30 minutes" is 25 minutes,
    // a number the author wrote. A timer that goes off early and gets extended is a better
    // failure than one that goes off late.
    private static readonly Regex DurationPattern = new(
        @"\b(?<value>\d+(?:\.\d+)?)\s*(?:(?:-|–|—|to)\s*\d+(?:\.\d+)?\s*)?"
        + @"(?<unit>seconds?|secs?|minutes?|mins?|hours?|hrs?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // An explicit scale is mandatory — see the class comment. The degree sign is optional
    // ("350 F", "180 degrees C"), the LETTER is not.
    private static readonly Regex TemperaturePattern = new(
        @"\b(?<value>-?\d{1,3})\s*(?:°\s*|degrees?\s*)?"
        + @"(?<unit>celsius|centigrade|fahrenheit|c|f)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The first duration mentioned, in whole seconds, or null. FIRST rather than largest or
    /// summed: a step reading "fry for 2 minutes, then simmer for 20" describes a sequence the
    /// author chose to write as one step, and 22 minutes is a number that appears nowhere in
    /// it. The leading figure is the one a cook starts with.
    /// </summary>
    public static int? ExtractDurationSeconds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = DurationPattern.Match(text);
        if (!match.Success
            || !double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || value <= 0)
        {
            return null;
        }

        var multiplier = match.Groups["unit"].Value.ToLowerInvariant() switch
        {
            var u when u.StartsWith("sec") => 1,
            var u when u.StartsWith("min") => 60,
            _ => 3600,
        };

        var seconds = (int)Math.Round(value * multiplier, MidpointRounding.AwayFromZero);
        if (seconds < 1)
        {
            return null;
        }

        // The same ceiling the validator and the generator use. Over it means the regex read
        // something that was not a duration, so drop rather than clamp — a clamped value would
        // assert a 24-hour step nobody described.
        return seconds > RecipeStepRules.MaxDurationSeconds ? null : seconds;
    }

    /// <summary>
    /// The first explicitly-scaled temperature, or null. Validated through the SAME
    /// <see cref="RecipeStepRules.TemperatureIsValid"/> the human write path uses, so an
    /// imported step can never carry a temperature a typed one could not.
    /// </summary>
    public static StepTemperature? ExtractTemperature(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (Match match in TemperaturePattern.Matches(text))
        {
            if (!int.TryParse(match.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }

            var unitText = match.Groups["unit"].Value.ToLowerInvariant();
            var unit = unitText is "f" or "fahrenheit" ? TemperatureUnit.Fahrenheit : TemperatureUnit.Celsius;

            var temperature = new StepTemperature { Value = value, Unit = unit };
            if (RecipeStepRules.TemperatureIsValid(temperature))
            {
                return temperature;
            }

            // Out of range for its scale — almost always a false positive, most often a lone
            // "c" that was really a word boundary rather than Celsius. Keep scanning; a later
            // match in the same sentence is frequently the real one.
        }

        return null;
    }
}
