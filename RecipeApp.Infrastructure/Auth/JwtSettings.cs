namespace RecipeApp.Infrastructure.Auth;

public class JwtSettings
{
    public const string SectionName = "Jwt";

    public string Key { get; set; } = null!;
    public string Issuer { get; set; } = null!;
    public string Audience { get; set; } = null!;

    /// <summary>
    /// Accounts (KAN-20): replaces the old 7-day `ExpiryDays`.
    ///
    /// The long lifetime was never a preference — it was forced, because there was no refresh
    /// and shortening it would only have made people sign in more often (ADR-0009 says so in
    /// as many words). Now that a refresh cookie exists, minutes is what a token stolen out of
    /// a browser is worth.
    /// </summary>
    public int AccessTokenMinutes { get; set; } = 15;
}
