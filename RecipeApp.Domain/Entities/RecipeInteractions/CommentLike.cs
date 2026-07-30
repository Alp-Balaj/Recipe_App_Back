namespace RecipeApp.Domain.Entities.RecipeInteractions;

// A like on a COMMENT, not a recipe — Like is recipe-only and stays that way. Same
// composite-key join shape as Like/SavedRecipe, which is what makes RankEvent
// CommentReceivedLike reachable for the first time.
public class CommentLike
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public Guid CommentId { get; set; }
    public Comment Comment { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
