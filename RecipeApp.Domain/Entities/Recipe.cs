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

    // Provenance for an IMPORTED recipe (stream L, decision D15 — 2026-08-06). The page the
    // recipe was read off, or null for every recipe that was typed or generated. Ordinary
    // nullable scalar, so it needs no OnModelCreating entry — the same treatment ImageUrl gets.
    //
    // IMMUTABLE ONCE SET, and the mechanism is absence rather than a guard: UpdateRecipeRequest
    // does not carry it and UpdateRecipeAsync assigns named fields, so there is no code path
    // that can write it a second time. An owner may rewrite every word of an imported recipe;
    // what they may not do is relabel where it came from, because the source domain is shown to
    // readers as a claim about attribution and an editable claim is not attribution.
    //
    // NOT a marker of AI involvement, and deliberately separate from IsAiGenerated. An import
    // that fell back to model extraction still read SOMEONE ELSE'S recipe off a real page — the
    // model transcribed, it did not invent — so IsAiGenerated stays false on every import path
    // and this column is the whole of what import claims about itself. Conflating the two would
    // badge a real author's recipe as generated, which is the one provenance error that
    // misattributes authorship rather than merely omitting it.
    public string? SourceUrl { get; set; }

    // Relations
    public ICollection<SavedRecipe> SavedByUsers { get; set; } = [];
    public ICollection<Like> Likes { get; set; } = [];
    public ICollection<Comment> Comments { get; set; } = [];
    public ICollection<CookedRecipe> CookedBy { get; set; } = [];
    public ICollection<MealPlanEntry> MealPlanEntries { get; set; } = [];
}