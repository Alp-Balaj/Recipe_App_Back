using RecipeApp.Domain.Enums;

namespace RecipeApp.Application.Auth.Dtos;

// Governor (stream D): Role rides both responses so the SPA can gate the Admin surface
// without a second call. Additive on the wire — older clients ignore the extra field.
public record AuthResponse(string Token, DateTime ExpiresAtUtc, Guid UserId, string Username, UserRole Role);

// GET /auth/me — the current identity read from the DB (not the token), so a username
// changed via PUT /users/me is reflected on the next boot validation rather than lingering
// as a stale JWT claim until expiry. Same for Role: a promotion shows up on the next boot
// even though the old token's role claim stays stale until re-login.
//
// Onboarding (stream K): NeedsOnboarding rides along for the same reason Role does — it is
// server truth the token cannot carry, and /auth/me is the one call every boot already
// makes. Appended with a default so existing positional constructions keep compiling.
// It is the negation of User.OnboardingCompletedAt, never "both preference lists are
// empty": a user whose honest answer was "no preferences" has finished onboarding.
//
// Accounts (KAN-21): SecondFactorResetEffectiveAtUtc rides along for a third reason of the
// same kind, and it is the mechanism behind "every live session is warned". An emailed
// second-factor reset is a 48-hour countdown somebody may have started against this account,
// and the person who can stop it is whoever is signed in — so the warning has to reach every
// session rather than one settings screen nobody has open. /auth/me is read on every boot and
// /auth/refresh answers with this same shape, so a countdown started now is visible to every
// live session within one access-token lifetime, without a poll or a socket.
//
// Appended with a default, like NeedsOnboarding before it, so existing positional
// constructions keep compiling.
public record MeResponse(
    Guid UserId,
    string Username,
    UserRole Role,
    bool NeedsOnboarding = false,
    DateTime? SecondFactorResetEffectiveAtUtc = null);
