using RecipeApp.Domain.Entities.RecipeInteractions;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;
using System.Xml.Linq;

namespace RecipeApp.Domain.Entities;

public class Recipe
{
    public Guid Id { get; set; }
    public string Title { get; set; } = null!;
    public string Description { get; set; } = null!;
    public int PrepTimeMinutes { get; set; }
    public int CookTimeMinutes { get; set; }
    public int TotalTimeMinutes => PrepTimeMinutes + CookTimeMinutes;
    public int Servings { get; set; }
    public DifficultyLevel Difficulty { get; set; }
    // Typed in stream G (D10). Nullable still means "belongs to no particular cuisine",
    // which is a different answer from Cuisine.Other ("a real cuisine, not on the list").
    public Cuisine? CuisineType { get; set; }
    public int? CaloriesPerServing { get; set; }
    public string? ImageUrl { get; set; }
    public RecipeVisibility Visibility { get; set; } = RecipeVisibility.Public;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    // Soft delete (excluded from queries via global query filter in ApplicationDbContext)
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }

    // Structured data (stored as jsonb)
    public List<RecipeIngredient> Ingredients { get; set; } = [];
    public List<RecipeStep> Steps { get; set; } = [];
    // Curated vocabulary since stream G (D10): tag filtering on GET /recipes is match-ALL
    // and case-sensitive, which free-text tags could never satisfy honestly.
    public List<RecipeTag> Tags { get; set; } = [];

    // Owner
    public Guid CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;

    // Provenance (stream E, decision D1 — 2026-07-30). A generated recipe is owned by the
    // user who asked for it, exactly like one they typed, and is marked rather than
    // segregated: PUT/DELETE stay owner-checked, the feed and the planner need no special
    // case. The flag is what pays for that — it is why generation awards no RecipeCreated
    // points (pressing "generate" must not farm rank), and it is what a reader sees on the
    // recipe. SourceConversationId points at the chat thread the request came out of, so
    // the provenance claim is auditable rather than a boolean assertion; null means the
    // recipe was generated outside a conversation, and it is nullable for the same reason
    // for AI recipes whose conversation is later hard-deleted.
    public bool IsAiGenerated { get; set; }
    public Guid? SourceConversationId { get; set; }

    // Relations
    public ICollection<SavedRecipe> SavedByUsers { get; set; } = [];
    public ICollection<Like> Likes { get; set; } = [];
    public ICollection<Comment> Comments { get; set; } = [];
    public ICollection<CookedRecipe> CookedBy { get; set; } = [];
    public ICollection<MealPlanEntry> MealPlanEntries { get; set; } = [];
}