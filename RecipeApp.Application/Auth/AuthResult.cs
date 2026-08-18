using RecipeApp.Application.Auth.Dtos;

namespace RecipeApp.Application.Auth;

public class AuthResult
{
    public bool Succeeded { get; }
    public string? Error { get; }
    public AuthResponse? Response { get; }

    /// <summary>
    /// Accounts (KAN-20): the session this sign-in opened, for the endpoint to set as cookies.
    /// Null on failure, and only on failure — every successful sign-in opens a session now.
    /// </summary>
    public SessionTokens? Tokens { get; }

    private AuthResult(bool succeeded, string? error, AuthResponse? response, SessionTokens? tokens)
    {
        Succeeded = succeeded;
        Error = error;
        Response = response;
        Tokens = tokens;
    }

    public static AuthResult Success(AuthResponse response, SessionTokens? tokens = null) =>
        new(true, null, response, tokens);

    public static AuthResult Failure(string error) => new(false, error, null, null);
}
