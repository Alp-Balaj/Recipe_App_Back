using RecipeApp.Application.Auth.Dtos;

namespace RecipeApp.Application.Auth;

public class AuthResult
{
    public bool Succeeded { get; }
    public string? Error { get; }
    public AuthResponse? Response { get; }

    private AuthResult(bool succeeded, string? error, AuthResponse? response)
    {
        Succeeded = succeeded;
        Error = error;
        Response = response;
    }

    public static AuthResult Success(AuthResponse response) => new(true, null, response);
    public static AuthResult Failure(string error) => new(false, error, null);
}
