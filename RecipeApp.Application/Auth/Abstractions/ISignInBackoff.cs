namespace RecipeApp.Application.Auth.Abstractions;

/// <summary>
/// Accounts (KAN-21, ADR-0008): the per-account memory of repeated sign-in failures.
///
/// The decision this interface exists to serve is the ABSENCE of a lockout. Nothing here
/// can put an account beyond reach — the only thing a failure buys is a wait on the next
/// attempt, escalating and then decaying. A reader looking for the lockout will not find
/// one, and adding one is a change to ADR-0008 rather than to this file.
///
/// It is orthogonal to <c>RateLimitPolicies.Auth</c>, which partitions by IP. That lane
/// cannot see that a thousand addresses are all guessing at one account; this one cannot
/// see that one address is guessing at a thousand accounts. Both are wanted.
/// </summary>
public interface ISignInBackoff
{
    /// <summary>
    /// How long the caller must wait before this key may try again, or null when it may try
    /// now. A caller that gets a value should answer 429 with it as Retry-After rather than
    /// holding the request open — a server that sleeps on demand is a denial of service
    /// anyone can aim at it.
    /// </summary>
    TimeSpan? RetryAfter(string key);

    /// <summary>Record one failed password or code attempt against this key.</summary>
    void RecordFailure(string key);

    /// <summary>Forget this key's failures. Called on every success, so a fumble costs nothing later.</summary>
    void Clear(string key);
}
