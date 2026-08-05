using System.Linq.Expressions;
using RecipeApp.Domain.Entities;
using RecipeApp.Domain.Enums;

namespace RecipeApp.Infrastructure.Recipes;

/// <summary>
/// The ONE definition of "which recipes may this caller read" (stream F, decision D6,
/// 2026-08-05). Every user-facing read path composes this expression rather than
/// hand-writing the predicate, because the failure mode of a divergent copy is showing
/// a private recipe to someone who should not see it.
///
/// The rule, in full:
///   * Public          — everyone, including anonymous guests.
///   * Private         — the author only.
///   * FriendsOnly     — the author, plus any viewer in a MUTUAL follow with the author:
///                       the viewer follows the author AND the author follows the viewer.
///                       A one-way follow in EITHER direction grants nothing. The two
///                       directional readings were rejected in D6 — "viewer follows author"
///                       lets anyone grant themselves access with one click, and "author
///                       follows viewer" silently widens an audience on a follow-back.
///
/// Anonymous callers take the explicit Public-only branch: a null id must never reach EF,
/// where `= NULL` is silently always-false rather than "no owner" (the same reason the
/// call sites used to branch by hand).
///
/// The mutual test reads off the author's two follow collections rather than a captured
/// DbSet, so the expression stays a pure <see cref="Expression"/> that any query can
/// compose. Per ApplicationDbContext's mapping:
///   User.Followers = UserFollow rows with FollowingId == that user  (people following them)
///   User.Following = UserFollow rows with FollowerId  == that user  (people they follow)
/// so "caller follows author" is author.Followers.Any(FollowerId == caller) and "author
/// follows caller" is author.Following.Any(FollowingId == caller). Both translate to
/// correlated EXISTS subqueries, each served by one of the two UserFollows indexes.
///
/// Composing onto something that is not a Recipe (comments, saved rows) does NOT mean
/// copying the predicate: project this to ids and use Contains — see the call sites in
/// SocialService. Admin reads are deliberately NOT built on this (decision D5);
/// AdminService owns its own IgnoreQueryFilters queries and is untouched by this policy.
/// </summary>
public static class RecipeVisibilityPolicy
{
    /// <summary>
    /// The visibility predicate for <paramref name="callerId"/> — null means an
    /// unauthenticated guest.
    /// </summary>
    public static Expression<Func<Recipe, bool>> VisibleTo(Guid? callerId)
    {
        if (callerId is not Guid caller)
        {
            return r => r.Visibility == RecipeVisibility.Public;
        }

        return r => r.Visibility == RecipeVisibility.Public
            || r.CreatedByUserId == caller
            || (r.Visibility == RecipeVisibility.FriendsOnly
                && r.CreatedByUser.Followers.Any(f => f.FollowerId == caller)
                && r.CreatedByUser.Following.Any(f => f.FollowingId == caller));
    }
}
