using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using RecipeApp.Application.Auth.Abstractions;
using RecipeApp.Domain.Entities;

namespace RecipeApp.Infrastructure.Auth;

public class JwtTokenService : IJwtTokenService
{
    // Governor (stream D): the token-version claim backing the revocation check. Custom
    // (short) name so neither JwtSecurityTokenHandler claim-type map touches it.
    public const string TokenVersionClaim = "tver";

    // Accounts (KAN-20): the session this token belongs to. Same custom-name reasoning as
    // "tver" above. It carries two jobs: the pipeline checks the row still exists (so dropping
    // one device bites its live access token rather than waiting out its lifetime), and the
    // devices list uses it to mark which row is the caller's own.
    public const string SessionIdClaim = "sid";

    private readonly JwtSettings _settings;

    public JwtTokenService(IOptions<JwtSettings> settings)
    {
        _settings = settings.Value;
    }

    public (string Token, DateTime ExpiresAtUtc) GenerateToken(User user, Guid? sessionId = null)
    {
        var expiresAtUtc = DateTime.UtcNow.AddMinutes(_settings.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.UniqueName, user.Username),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            // Governor (stream D): ClaimTypes.Role deliberately, not a bare "role" — the
            // handler's outbound map shortens it to "role" in the JWT and the inbound map
            // restores ClaimTypes.Role on validation, which is what the default
            // RoleClaimType (and so RequireRole / the AdminOnly policy) actually reads.
            new Claim(ClaimTypes.Role, user.Role.ToString()),
            new Claim(TokenVersionClaim, user.TokenVersion.ToString()),
        };

        // Omitted only for a caller with no session row. Nothing in the app is that after
        // KAN-20 — but the claim's ABSENCE is meaningful to the pipeline (it is how a token
        // issued before this phase is recognised and let through), so it is never faked.
        if (sessionId is Guid id)
        {
            claims.Add(new Claim(SessionIdClaim, id.ToString()));
        }

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_settings.Key));
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _settings.Issuer,
            audience: _settings.Audience,
            claims: claims,
            expires: expiresAtUtc,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAtUtc);
    }
}
