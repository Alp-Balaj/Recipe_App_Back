namespace RecipeApp.Domain.Enums;

// What happened. Stored as text like every other enum here, so a new member is
// additive and an old row stays readable.
//
// Deliberately NOT one member per future feature: a type only exists once something
// can actually produce it, which is the mistake RankEvent made — two of its six
// members sat unreachable for months because they were named before they were built.
public enum NotificationType
{
    RecipeLiked,
    RecipeCommented,
    CommentLiked,
    UserFollowed,
}
