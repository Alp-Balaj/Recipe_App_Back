using RecipeApp.Application.Moderation.Dtos;
using RecipeApp.Application.Moderation.Validators;
using RecipeApp.Domain.Enums;

namespace RecipeApp.UnitTests;

public class CreateReportRequestValidatorTests
{
    private readonly CreateReportRequestValidator _validator = new();

    private static CreateReportRequest Valid() =>
        new(ReportTargetType.Recipe, Guid.NewGuid(), ReportReason.Spam, "Looks like an ad.");

    [Fact]
    public void Valid_request_passes()
    {
        Assert.True(_validator.Validate(Valid()).IsValid);
    }

    [Fact]
    public void Null_details_pass()
    {
        Assert.True(_validator.Validate(Valid() with { Details = null }).IsValid);
    }

    [Fact]
    public void Empty_target_id_fails()
    {
        Assert.False(_validator.Validate(Valid() with { TargetId = Guid.Empty }).IsValid);
    }

    // Numeric enum values outside the range bind silently (JsonStringEnumConverter only
    // guards the string form), so IsInEnum is what turns them into a 400.
    [Fact]
    public void Undefined_target_type_fails()
    {
        Assert.False(_validator.Validate(Valid() with { TargetType = (ReportTargetType)99 }).IsValid);
    }

    [Fact]
    public void Undefined_reason_fails()
    {
        Assert.False(_validator.Validate(Valid() with { Reason = (ReportReason)99 }).IsValid);
    }

    [Fact]
    public void Details_over_1000_chars_fail()
    {
        Assert.False(_validator.Validate(Valid() with { Details = new string('x', 1001) }).IsValid);
    }
}
