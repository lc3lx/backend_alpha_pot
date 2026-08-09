using Microsoft.AspNetCore.Http;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Domain.Enums;
using System.Security.Claims;

namespace ScarAlpha.Infrastructure.Auth;

public sealed class HttpCurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _http;

    public HttpCurrentUser(IHttpContextAccessor http) => _http = http;

    public Guid UserId
    {
        get
        {
            var sub = _http.HttpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier)
                      ?? _http.HttpContext?.User?.FindFirstValue("sub");
            if (Guid.TryParse(sub, out var id))
                return id;
            throw new ApiException(ApiErrorCodes.Unauthorized, "Missing authenticated user.", 401);
        }
    }

    public long TelegramUserId
    {
        get
        {
            var raw = _http.HttpContext?.User?.FindFirstValue("telegram_user_id");
            if (long.TryParse(raw, out var id))
                return id;
            throw new ApiException(ApiErrorCodes.Unauthorized, "Missing telegram identity.", 401);
        }
    }

    public bool IsAdmin
    {
        get
        {
            var user = _http.HttpContext?.User;
            if (user is null) return false;
            if (user.IsInRole(nameof(UserRole.Admin))) return true;
            var role = user.FindFirstValue(ClaimTypes.Role) ?? user.FindFirstValue("role");
            return string.Equals(role, nameof(UserRole.Admin), StringComparison.OrdinalIgnoreCase);
        }
    }
}
