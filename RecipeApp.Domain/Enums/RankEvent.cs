namespace RecipeApp.Domain.Enums;

public enum RankEvent
{
    RecipeCreated,
    RecipeReceivedLike,
    RecipeReceivedComment,
    CommentReceivedLike,
    SavedByOtherUser,
    AiRecipeCookedAndRated
}