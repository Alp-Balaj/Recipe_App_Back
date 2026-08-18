namespace RecipeApp.Application.Auth;

// Accounts (KAN-20): the pair of secrets a freshly opened or freshly rotated session hands
// back. Deliberately NOT cookies — this layer does not know what a cookie is, and the same
// pair is what a non-browser client would want in a body. The API layer decides they travel
// as `httpOnly` cookies (see SessionCookies).
//
// The two lifetimes differ on purpose and are the whole point of the phase: the access token
// is minutes long, so a leaked one is worth almost nothing, and the refresh token is the
// long-lived half that never reaches JavaScript.
public record SessionTokens(
    string AccessToken,
    DateTime AccessExpiresAtUtc,
    // Null means "the refresh cookie the caller already holds is still the right one", which
    // happens to the losing half of a two-tab refresh race — its sibling's response already
    // replaced the shared cookie. Writing anything at all there would overwrite a good cookie
    // with a superseded one, so the endpoint must leave it alone.
    string? RefreshToken,
    DateTime RefreshExpiresAtUtc);
