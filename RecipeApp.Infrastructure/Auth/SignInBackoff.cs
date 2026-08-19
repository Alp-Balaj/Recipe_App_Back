using System.Collections.Concurrent;
using RecipeApp.Application.Auth.Abstractions;

namespace RecipeApp.Infrastructure.Auth;

// Accounts (KAN-21, ADR-0008): "repeated failures slow down rather than lock out".
//
// Three free failures, then the next attempt waits 2s, 4s, 8s … up to five minutes, and the
// whole memory of an account evaporates after thirty quiet minutes.
//
// WHY IN MEMORY, AND WHAT THAT COSTS. ADR-0009 already records that this state is
// in-process and forgotten on restart. That is accepted for a single Railway service that
// restarts on deploy — an attacker who could force restarts on demand could reset the curve,
// which is a smaller problem than a per-attempt database write on the sign-in path. It is
// also, explicitly, one of the things that makes running a second instance non-trivial: two
// instances would each hold half the failures and each think the curve was flatter than it is.
//
// WHY A SINGLETON DICTIONARY AND NOT IMemoryCache. The decay is not a cache eviction, it is
// a rule with a number in it, and a sliding TTL would leave the rule's most interesting
// property — that thirty quiet minutes forgets everything — untestable without waiting
// thirty minutes. Here it is arithmetic over a stored timestamp, and the `now` overloads
// below are how the tests move it.
public sealed class SignInBackoff : ISignInBackoff
{
    /// <summary>Failures that cost nothing. A person who mistypes their own password twice is not an attack.</summary>
    public const int FreeFailures = 3;

    /// <summary>The wait imposed by the first failure past the free ones. It doubles from here.</summary>
    public static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The ceiling. Five minutes is long enough that sustained guessing stops being a
    /// strategy and short enough that a genuinely stuck person can wait it out — and they
    /// do not have to, because the recovery paths stay open while it runs.
    /// </summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(5);

    /// <summary>Quiet time after which the account's failures are forgotten entirely.</summary>
    public static readonly TimeSpan Decay = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The largest table this will hold before it sweeps the decayed entries out of it. One
    /// small record per account currently failing — the sweep exists so a script hammering
    /// fabricated usernames cannot turn a fixed-size defence into unbounded memory.
    /// </summary>
    private const int SweepThreshold = 10_000;

    private readonly ConcurrentDictionary<string, Attempt> _attempts = new(StringComparer.Ordinal);

    private sealed record Attempt(int Failures, DateTime LastFailureUtc);

    /// <summary>
    /// The wait a given number of failures earns. Pure, and the only place the curve is
    /// written down.
    ///
    /// Deliberately saturating rather than shifting into overflow: at 34 failures a naive
    /// <c>1 &lt;&lt; n</c> is negative, and a negative delay is an open door dressed as a lock.
    /// </summary>
    public static TimeSpan DelayAfter(int failures)
    {
        if (failures <= FreeFailures)
        {
            return TimeSpan.Zero;
        }

        var doublings = failures - FreeFailures - 1;
        var seconds = FirstDelay.TotalSeconds * Math.Pow(2, Math.Min(doublings, 30));
        return seconds >= MaxDelay.TotalSeconds ? MaxDelay : TimeSpan.FromSeconds(seconds);
    }

    public TimeSpan? RetryAfter(string key) => RetryAfter(key, DateTime.UtcNow);

    public void RecordFailure(string key) => RecordFailure(key, DateTime.UtcNow);

    public void Clear(string key) => _attempts.TryRemove(key, out _);

    // ── The `now` overloads ────────────────────────────────────────────────────────
    //
    // There is no clock abstraction in this solution and this feature does not introduce one
    // (KAN-19 and KAN-20 both declined to, and ~30 sites read DateTime.UtcNow directly).
    // Elsewhere that is fine because the state under test is a ROW whose timestamps a test
    // can rewrite. This state is not a row, so the seam has to be here instead: the tests
    // pass the moment, production passes DateTime.UtcNow, and the rule itself is written once.

    public TimeSpan? RetryAfter(string key, DateTime now)
    {
        if (!_attempts.TryGetValue(key, out var attempt) || Decayed(attempt, now))
        {
            return null;
        }

        var readyAt = attempt.LastFailureUtc.Add(DelayAfter(attempt.Failures));
        return readyAt > now ? readyAt - now : null;
    }

    public void RecordFailure(string key, DateTime now)
    {
        if (_attempts.Count >= SweepThreshold)
        {
            Sweep(now);
        }

        _attempts.AddOrUpdate(
            key,
            _ => new Attempt(1, now),
            // A decayed entry starts over rather than resuming where it left off — that IS
            // the decay, and continuing the old curve after thirty quiet minutes would make
            // the rule a lifetime counter with extra steps.
            (_, existing) => Decayed(existing, now)
                ? new Attempt(1, now)
                : new Attempt(existing.Failures + 1, now));
    }

    private static bool Decayed(Attempt attempt, DateTime now) => attempt.LastFailureUtc.Add(Decay) <= now;

    private void Sweep(DateTime now)
    {
        foreach (var (key, attempt) in _attempts)
        {
            if (Decayed(attempt, now))
            {
                _attempts.TryRemove(key, out _);
            }
        }
    }

    /// <summary>
    /// The key a password attempt against an UNKNOWN identifier is counted under.
    ///
    /// That an unknown identifier gets a key at all is not politeness — it is what keeps the
    /// throttle from becoming the account-enumeration oracle LoginAsync's dummy password
    /// verification exists to avoid. If only real accounts accrued failures, a 429 would mean
    /// "this account exists" and a 401 would mean "it does not", handing an attacker exactly
    /// the answer the rest of that path refuses to give.
    /// </summary>
    public static string KeyForIdentifier(string usernameOrEmail) =>
        $"signin:{usernameOrEmail?.Trim().ToLowerInvariant()}";

    /// <summary>
    /// The key a password attempt against a KNOWN account is counted under.
    ///
    /// By account rather than by the string that was typed, because an account has two names:
    /// a username and an email address, and both sign in. Counting per string would hand an
    /// attacker two independent free-failure allowances and two independent curves for one
    /// victim, just for alternating between them — which is a doubling of the guessing budget
    /// available for the cost of noticing.
    /// </summary>
    public static string KeyForPassword(Guid userId) => $"password:{userId}";

    /// <summary>
    /// The key a CODE attempt is counted under. Separate from the password curve above on
    /// purpose: a person who fumbles their password and then their code is one person having
    /// one bad minute, and compounding the two would make an honest mistake cost twice.
    /// </summary>
    public static string KeyForAccount(Guid userId) => $"challenge:{userId}";
}
