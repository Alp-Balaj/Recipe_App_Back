using RecipeApp.Application.MealPlanning.Dtos;
using RecipeApp.Application.MealPlanning.Validators;

namespace RecipeApp.UnitTests;

// KAN-6's shape rules for POST /cook-log, at the seam where they actually live.
//
// The DATE BOUNDS are deliberately absent from this file, because they are deliberately absent
// from the validator: "before your account existed" is a database question and a request
// validator sees the body and nothing else. Both bounds live in CookLogService.LogAsync and are
// tested end-to-end in PastCookEndpointsTests — look there, not here, when a date is refused.
public class LogCookRequestValidatorTests
{
    private readonly LogCookRequestValidator _validator = new();

    private static readonly Guid Recipe = Guid.NewGuid();

    [Fact]
    public void Validate_NoDate_IsValid()
    {
        // The pre-KAN-6 request, still the common one: cook mode and MealCard send this and mean
        // "now". Nothing about the new fields may make an omitted date look like a malformed one.
        var result = _validator.Validate(new LogCookRequest(Recipe, null));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_UtcDate_IsValid()
    {
        var cookedAt = new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc);

        var result = _validator.Validate(new LogCookRequest(Recipe, null, cookedAt));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_UtcDateAtAnyTimeOfDay_IsValid()
    {
        // Only the KIND is this file's business. Unlike WeekStartDate — a bucket key, so a
        // non-midnight value there is a client that has misunderstood the field — a cook's
        // timestamp carries a real time of day, and the service reduces it to its date. Rejecting
        // 23:30 here would refuse a correct request for being differently correct.
        var cookedAt = new DateTime(2026, 3, 5, 23, 30, 0, DateTimeKind.Utc);

        var result = _validator.Validate(new LogCookRequest(Recipe, null, cookedAt));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    public void Validate_NonUtcDate_Fails(DateTimeKind kind)
    {
        // Npgsql rejects a non-UTC DateTime against timestamptz, so without this the request is
        // a 500 rather than a 400 — the same trap WeekStart and MarkNotificationsRead guard.
        var cookedAt = DateTime.SpecifyKind(new DateTime(2026, 3, 5, 12, 0, 0), kind);

        var result = _validator.Validate(new LogCookRequest(Recipe, null, cookedAt));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("UTC", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_NoteAtTheColumnWidth_IsValid()
    {
        var result = _validator.Validate(new LogCookRequest(Recipe, null, null, new string('x', 500)));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_NoteOverTheColumnWidth_Fails()
    {
        // 500 is CookLog.Note's HasMaxLength, matching UpdateCookNoteRequestValidator exactly:
        // a note that PATCH would refuse must not be accepted by POST and hit the column instead.
        var result = _validator.Validate(new LogCookRequest(Recipe, null, null, new string('x', 501)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(LogCookRequest.Note));
    }

    [Fact]
    public void Validate_EmptyMealPlanEntryId_StillFails()
    {
        // The rule that was already here, re-pinned because appending two optional parameters to
        // a positional record is exactly the change that silently re-binds an argument.
        var result = _validator.Validate(new LogCookRequest(Recipe, Guid.Empty));

        Assert.False(result.IsValid);
    }
}
