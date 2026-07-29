using RecipeApp.Application.MealPlanning;
using RecipeApp.Application.MealPlanning.Dtos;
using RecipeApp.Application.MealPlanning.Validators;

namespace RecipeApp.UnitTests;

// Direct tests of the mark upsert's validation rules (week/shopping rework, Task 3). Follows
// AddShoppingListItemRequestValidatorTests' idiom. Note what is deliberately NOT validated:
// whether the key currently exists in the projection. A mark for an absent key is legal — that
// is what makes the write safe against a plan edit landing between the read and the tick.
public class SetShoppingListMarkRequestValidatorTests
{
    private readonly SetShoppingListMarkRequestValidator _validator = new();

    // 2026-08-03 is a Monday.
    private static readonly DateTime Monday = new(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc);

    private static SetShoppingListMarkRequest Request(
        string key = "flour", bool isPurchased = true, bool isSuppressed = false, DateTime? weekStartDate = null) =>
        new(weekStartDate ?? Monday, key, isPurchased, isSuppressed);

    [Fact]
    public void Validate_WellFormedRequest_IsValid()
    {
        Assert.True(_validator.Validate(Request()).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_BlankKey_Fails(string key)
    {
        var result = _validator.Validate(Request(key: key));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(SetShoppingListMarkRequest.Key));
    }

    // 200 is ShoppingListMark.Key's HasMaxLength(200) — over the cap the insert would throw
    // rather than 400.
    [Fact]
    public void Validate_KeyAtLengthCap_IsValid()
    {
        Assert.True(_validator.Validate(Request(key: new string('a', 200))).IsValid);
    }

    [Fact]
    public void Validate_KeyOverLengthCap_Fails()
    {
        var result = _validator.Validate(Request(key: new string('a', 201)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(SetShoppingListMarkRequest.Key));
    }

    // --- the Global Constraint: UTC-midnight MONDAY ---------------------------------------

    [Fact]
    public void Validate_UtcMidnightMonday_IsValid()
    {
        Assert.True(_validator.Validate(Request(weekStartDate: Monday)).IsValid);
    }

    [Fact]
    public void Validate_MidnightOnANonMonday_Fails()
    {
        var result = _validator.Validate(Request(weekStartDate: Monday.AddDays(2)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(SetShoppingListMarkRequest.WeekStartDate));
    }

    [Fact]
    public void Validate_MondayWithATimeComponent_Fails()
    {
        var result = _validator.Validate(Request(weekStartDate: Monday.AddHours(9)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(SetShoppingListMarkRequest.WeekStartDate));
    }

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    public void Validate_NonUtcKind_Fails(DateTimeKind kind)
    {
        var result = _validator.Validate(Request(weekStartDate: DateTime.SpecifyKind(Monday, kind)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(SetShoppingListMarkRequest.WeekStartDate));
    }

    // --- suppression is meaningless for a manual row --------------------------------------

    [Fact]
    public void Validate_SuppressingAManualKey_Fails()
    {
        var result = _validator.Validate(Request(key: ShoppingListKeys.ForManual(Guid.NewGuid()), isPurchased: false, isSuppressed: true));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(SetShoppingListMarkRequest.IsSuppressed));
    }

    [Fact]
    public void Validate_PurchasingAManualKey_IsValid()
    {
        // Only suppression is rejected — a manual row is tickable like anything else.
        Assert.True(_validator.Validate(
            Request(key: ShoppingListKeys.ForManual(Guid.NewGuid()), isPurchased: true, isSuppressed: false)).IsValid);
    }

    [Fact]
    public void Validate_SuppressingADerivedKey_IsValid()
    {
        Assert.True(_validator.Validate(Request(key: "olive oil", isPurchased: false, isSuppressed: true)).IsValid);
    }
}
