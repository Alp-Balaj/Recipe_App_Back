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

    // Stream G turned off allowIntegerValues, so a numeric value on the WIRE no longer
    // binds at all. IsInEnum still earns its place for the cast path these tests use —
    // (ReportTargetType)99 never went through a deserializer.
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
