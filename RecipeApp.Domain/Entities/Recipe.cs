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
    public string? CuisineType { get; set; }
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
    public List<string> Tags { get; set; } = [];

    // Owner
    public Guid CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;

    // Relations
    public ICollection<SavedRecipe> SavedByUsers { get; set; } = [];
    public ICollection<Like> Likes { get; set; } = [];
    public ICollection<Comment> Comments { get; set; } = [];
    public ICollection<CookedRecipe> CookedBy { get; set; } = [];
    public ICollection<MealPlanEntry> MealPlanEntries { get; set; } = [];
}