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

    private readonly JwtSettings _settings;

    public JwtTokenService(IOptions<JwtSettings> settings)
    {
        _settings = settings.Value;
    }

    public (string Token, DateTime ExpiresAtUtc) GenerateToken(User user)
    {
        var expiresAtUtc = DateTime.UtcNow.AddDays(_settings.ExpiryDays);

        var claims = new[]
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
