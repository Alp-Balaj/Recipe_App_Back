namespace RecipeApp.Domain.Entities;

/// <summary>
/// One user decision about one ingredient in one week: bought, or hidden.
///
/// Deliberately NOT stored on the shopping-list rows the plan produces, because those
/// rows no longer exist — the list is a projection recomputed on every read. Keying the
/// decision by (user, week, normalised ingredient) instead is what makes a tick survive
/// any plan edit: adding a meal on Wednesday cannot disturb a mark it never touched.
///
/// IsSuppressed is the lightweight pantry: "I already have olive oil" hides the group for
/// THIS week and it returns next week. Manual items are deleted for real, never suppressed.
/// </summary>
public class ShoppingListMark
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>UTC-midnight Monday. Scopes the decision to one shop.</summary>
    public DateTime WeekStartDate { get; set; }

    /// <summary>The IngredientKey.For(...) value — never a raw typed name.</summary>
    public string Key { get; set; } = null!;

    public bool IsPurchased { get; set; }
    public bool IsSuppressed { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
