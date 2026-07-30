using RecipeApp.Domain.Enums;

namespace RecipeApp.Application.Auth.Dtos;

// Governor (stream D): Role rides both responses so the SPA can gate the Admin surface
// without a second call. Additive on the wire — older clients ignore the extra field.
public record AuthResponse(string Token, DateTime ExpiresAtUtc, Guid UserId, string Username, UserRole Role);

// GET /auth/me — the current identity read from the DB (not the token), so a username
// changed via PUT /users/me is reflected on the next boot validation rather than lingering
// as a stale JWT claim until expiry. Same for Role: a promotion shows up on the next boot
// even though the old token's role claim stays stale until re-login.
public record MeResponse(Guid UserId, string Username, UserRole Role);
