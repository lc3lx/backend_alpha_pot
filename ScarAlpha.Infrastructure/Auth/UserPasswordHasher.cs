using Microsoft.AspNetCore.Identity;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Domain.Entities;

namespace ScarAlpha.Infrastructure.Auth;

public sealed class UserPasswordHasher : IUserPasswordHasher
{
    private readonly PasswordHasher<User> _hasher = new();

    public string Hash(string password) => _hasher.HashPassword(null!, password);

    public bool Verify(string hash, string password)
    {
        var result = _hasher.VerifyHashedPassword(null!, hash, password);
        return result is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
    }
}
