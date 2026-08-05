namespace RecipeApp.IntegrationTests;

/// <summary>
/// Follow-graph setup for the FriendsOnly visibility tests (stream F, decision D6).
///
/// The rule under test is MUTUAL: a viewer reads an author's FriendsOnly recipe only when
/// the viewer follows the author AND the author follows the viewer. Every one of the three
/// arrangements below is named after the relationship it builds rather than the calls it
/// makes, because the negative cases are the point — a test that says
/// <c>FollowOneWayAsync</c> cannot be misread later as "they are friends".
/// </summary>
public static class FollowTestHelper
{
    /// <summary>One-way: <paramref name="followerClient"/>'s user follows <paramref name="targetUserId"/>, and that is all.</summary>
    public static async Task FollowOneWayAsync(HttpClient followerClient, Guid targetUserId)
    {
        var response = await followerClient.PostAsync($"/users/{targetUserId}/follow", null);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Mutual: each user follows the other. This is the ONLY arrangement that grants
    /// FriendsOnly access to a non-author.
    /// </summary>
    public static async Task MakeMutualAsync(HttpClient clientA, Guid userAId, HttpClient clientB, Guid userBId)
    {
        await FollowOneWayAsync(clientA, userBId);
        await FollowOneWayAsync(clientB, userAId);
    }

    /// <summary>Breaks a follow edge — used to prove access is revoked when a mutual follow ends.</summary>
    public static async Task UnfollowAsync(HttpClient followerClient, Guid targetUserId)
    {
        var response = await followerClient.DeleteAsync($"/users/{targetUserId}/follow");
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Builds the named relationship between a viewer and an author. Exists so a
    /// visibility test can be a [Theory] over ALL FOUR arrangements — the three that must
    /// deny and the one that must allow — instead of a single happy path plus a stranger.
    /// A read path that only tested Stranger and Mutual would miss exactly the two
    /// directional readings D6 rejected.
    /// </summary>
    public static async Task ArrangeAsync(
        FollowRelationship relationship,
        HttpClient viewerClient, Guid viewerId,
        HttpClient authorClient, Guid authorId)
    {
        switch (relationship)
        {
            case FollowRelationship.Stranger:
                break;
            case FollowRelationship.ViewerFollowsAuthor:
                await FollowOneWayAsync(viewerClient, authorId);
                break;
            case FollowRelationship.AuthorFollowsViewer:
                await FollowOneWayAsync(authorClient, viewerId);
                break;
            case FollowRelationship.Mutual:
                await MakeMutualAsync(viewerClient, viewerId, authorClient, authorId);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(relationship));
        }
    }
}

/// <summary>Every arrangement of the follow graph between a viewer and an author.</summary>
public enum FollowRelationship
{
    /// <summary>Neither follows the other.</summary>
    Stranger,

    /// <summary>One-way: the viewer follows the author. Grants nothing (D6).</summary>
    ViewerFollowsAuthor,

    /// <summary>One-way: the author follows the viewer. Grants nothing (D6).</summary>
    AuthorFollowsViewer,

    /// <summary>Both directions — the ONLY arrangement that reveals FriendsOnly.</summary>
    Mutual,
}
