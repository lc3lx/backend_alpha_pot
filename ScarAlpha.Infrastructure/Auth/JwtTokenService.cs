using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Domain.Entities;
using ScarAlpha.Domain.Enums;

namespace ScarAlpha.Infrastructure.Auth;

public sealed class JwtTokenService : IJwtTokenService
{
    private readonly string _secret;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly int _expiryMinutes;

    public JwtTokenService(IConfiguration configuration)
    {
        _secret = configuration["JWT_SECRET"]
                  ?? configuration["Jwt:Secret"]
                  ?? throw new InvalidOperationException("JWT_SECRET is required.");
        _issuer = configuration["JWT_ISSUER"] ?? configuration["Jwt:Issuer"] ?? "ScarAlpha";
        _audience = configuration["JWT_AUDIENCE"] ?? configuration["Jwt:Audience"] ?? "ScarAlpha.App";
        _expiryMinutes = configuration.GetValue("Jwt:ExpiryMinutes", 60 * 24 * 7);
    }

    public string CreateToken(User user)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Role, user.Role.ToString()),
            new("role", user.Role.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        if (user.TelegramUserId.HasValue)
            claims.Add(new Claim("telegram_user_id", user.TelegramUserId.Value.ToString()));
        if (!string.IsNullOrWhiteSpace(user.Email))
            claims.Add(new Claim(JwtRegisteredClaimNames.Email, user.Email));

        if (!string.IsNullOrWhiteSpace(user.Username))
            claims.Add(new Claim("username", user.Username));

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(_expiryMinutes),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
