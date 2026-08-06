using RecipeApp.Application.Recipes.Import;
using RecipeApp.Domain.Enums;

namespace RecipeApp.UnitTests;

// Stream L. Stream J made a step's duration and temperature typed fields; import has to
// recover them from prose the source wrote for a human. These tests pin both what it reads and
// — more importantly — the two things it refuses to guess.
public class StepDetailExtractorTests
{
    [Theory]
    [InlineData("Simmer for 20 minutes.", 1200)]
    [InlineData("Rest for 1 hour before slicing.", 3600)]
    [InlineData("Blanch for 30 seconds.", 30)]
    [InlineData("Cook for 5 mins, stirring.", 300)]
    [InlineData("Chill for 2 hrs.", 7200)]
    [InlineData("Knead for 1.5 minutes.", 90)]
    public void Reads_a_duration(string text, int expected)
    {
        Assert.Equal(expected, StepDetailExtractor.ExtractDurationSeconds(text));
    }

    // The low end, matching how ingredient ranges are read. A timer that rings early and gets
    // extended beats one that rings after the food is done.
    [Fact]
    public void Duration_range_takes_the_low_end()
    {
        Assert.Equal(1500, StepDetailExtractor.ExtractDurationSeconds("Bake for 25-30 minutes."));
    }

    // FIRST, not summed. 22 minutes appears nowhere in this sentence, and a step total built
    // from arithmetic the author did not do is a number the app invented.
    [Fact]
    public void Takes_the_first_duration_when_a_step_lists_several()
    {
        Assert.Equal(120, StepDetailExtractor.ExtractDurationSeconds("Fry for 2 minutes, then simmer for 20 minutes."));
    }

    [Theory]
    [InlineData("Preheat the oven to 180°C.", 180, TemperatureUnit.Celsius)]
    [InlineData("Preheat the oven to 350°F.", 350, TemperatureUnit.Fahrenheit)]
    [InlineData("Heat to 200 degrees Celsius.", 200, TemperatureUnit.Celsius)]
    [InlineData("Bake at 425 F until golden.", 425, TemperatureUnit.Fahrenheit)]
    public void Reads_a_scaled_temperature(string text, int value, TemperatureUnit unit)
    {
        var result = StepDetailExtractor.ExtractTemperature(text);

        Assert.NotNull(result);
        Assert.Equal(value, result.Value);
        Assert.Equal(unit, result.Unit);
    }

    // THE REFUSAL THAT MATTERS MOST. A bare degree sign names no scale, and the tempting
    // heuristic ("over 250 must be Fahrenheit") is right often enough to be trusted and wrong
    // often enough to burn dinner. Null is the honest answer; the prose still says "180°" and
    // a cook reads it fine.
    [Theory]
    [InlineData("Preheat the oven to 180°.")]
    [InlineData("Heat to 350 degrees.")]
    public void Refuses_a_temperature_with_no_scale(string text)
    {
        Assert.Null(StepDetailExtractor.ExtractTemperature(text));
    }

    // Gas mark is a real UK scale with no member in TemperatureUnit. Converting it would
    // invent a precision the source never had.
    [Fact]
    public void Ignores_gas_mark()
    {
        Assert.Null(StepDetailExtractor.ExtractTemperature("Bake at gas mark 4."));
    }

    // Out of RecipeStepRules' plausible range for its scale — almost always a false positive,
    // so the scan continues rather than returning something absurd.
    [Fact]
    public void Skips_an_implausible_match_and_keeps_looking()
    {
        var result = StepDetailExtractor.ExtractTemperature("Add 900 c of stock, then bake at 180 C.");

        Assert.NotNull(result);
        Assert.Equal(180, result.Value);
        Assert.Equal(TemperatureUnit.Celsius, result.Unit);
    }

    [Fact]
    public void Finds_both_in_one_sentence()
    {
        const string text = "Bake at 180°C for 25 minutes.";

        Assert.Equal(1500, StepDetailExtractor.ExtractDurationSeconds(text));
        Assert.Equal(180, StepDetailExtractor.ExtractTemperature(text)!.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Season to taste.")]
    public void Returns_null_when_there_is_nothing_to_read(string? text)
    {
        Assert.Null(StepDetailExtractor.ExtractDurationSeconds(text));
        Assert.Null(StepDetailExtractor.ExtractTemperature(text));
    }
}
