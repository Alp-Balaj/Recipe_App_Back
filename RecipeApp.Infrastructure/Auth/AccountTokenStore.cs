using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RecipeApp.Domain.Entities;
using RecipeApp.Domain.Enums;
using RecipeApp.Infrastructure.Persistence;

namespace RecipeApp.Infrastructure.Auth;

// Accounts: the mechanics of an emailed single-use link, shared by everything that sends one.
//
// Lifted out of AccountRecoveryService when KAN-21's second-factor reset became a third
// purpose, for the same reason SecretTokens was lifted out of it when refresh tokens became a
// second caller: two independently maintained implementations of "issue a link, then find it
// again" is how one of them quietly loses the resend cooldown or starts storing plaintext.
//
// The three rules that live here, and nowhere else:
//
//   * The plaintext is returned to the caller and NEVER written. What the table holds is a
//     SHA-256 digest, and finding a row means hashing the candidate — so a leak of the table
//     yields nothing anyone can click.
//   * Issuing a purpose DELETES that user's outstanding tokens of the same purpose, in the
//     same SaveChanges as the insert. Only the newest link in a mailbox works, and there is
//     no window where the user has none at all.
//   * A link issued inside the cooldown suppresses the next one, and the caller returns as
//     though it had sent. That is a per-ACCOUNT limit protecting one inbox and one sending
//     reputation, which the IP-partitioned /auth rate limit cannot do.
internal static class AccountTokenStore
{
    /// <summary>
    /// How soon after issuing a link another of the same purpose will be issued to the same
    /// account. See the header: this exists for the mailbox, not for brute force.
    /// </summary>
    public static readonly TimeSpan ResendCooldown = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Issue a token, invalidating any outstanding one of the same purpose. Null means the
    /// cooldown suppressed it — the caller must answer exactly as it would have on the
    /// sending path, or the difference becomes an oracle.
    /// </summary>
    public static async Task<(string Plaintext, AccountToken Token)?> IssueAsync(
        ApplicationDbContext db,
        Guid userId,
        AccountTokenPurpose purpose,
        TimeSpan lifetime,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // A TRACKED delete, not ExecuteDelete: it shares one SaveChanges with the insert
        // below, and it keeps this path runnable on the in-memory provider the service-level
        // tests use. The set is at most one row.
        var outstanding = await db.AccountTokens
            .Where(t => t.UserId == userId && t.Purpose == purpose && t.ConsumedAt == null)
            .ToListAsync(cancellationToken);

        if (outstanding.Any(t => t.CreatedAt > DateTime.UtcNow - ResendCooldown))
        {
            logger.LogInformation(
                "Suppressed a {Purpose} link for user {UserId}: one was issued within the cooldown.",
                purpose, userId);
            return null;
        }

        db.AccountTokens.RemoveRange(outstanding);

        var plaintext = SecretTokens.Generate();
        var token = new AccountToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Purpose = purpose,
            TokenHash = SecretTokens.Hash(plaintext),
            ExpiresAtUtc = DateTime.UtcNow.Add(lifetime),
        };

        db.AccountTokens.Add(token);
        await db.SaveChangesAsync(cancellationToken);

        return (plaintext, token);
    }

    /// <summary>
    /// Look a candidate up by DIGEST — the plaintext is never in the table, so this is the
    /// only way to find a row, and a stolen table cannot be turned back into links.
    /// </summary>
    public static async Task<AccountToken?> FindAsync(
        ApplicationDbContext db, string plaintext, AccountTokenPurpose purpose, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(plaintext))
        {
            return null;
        }

        var hash = SecretTokens.Hash(plaintext);
        return await db.AccountTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.Purpose == purpose, cancellationToken);
    }
}
