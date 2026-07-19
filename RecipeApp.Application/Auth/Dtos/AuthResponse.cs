namespace RecipeApp.Application.Auth.Dtos;

public record AuthResponse(string Token, DateTime ExpiresAtUtc, Guid UserId, string Username);

// GET /auth/me — the current identity read from the DB (not the token), so a username
// changed via PUT /users/me is reflected on the next boot validation rather than lingering
// as a stale JWT claim until expiry.
public record MeResponse(Guid UserId, string Username);
