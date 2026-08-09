using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;

namespace ScarAlpha.Infrastructure.Telegram;

/// <summary>
/// Validates Telegram Mini App initData per official WebApp HMAC algorithm.
/// </summary>
public sealed class TelegramAuthService : ITelegramAuthService
{
    private readonly string _botToken;
    private readonly TimeSpan _maxAge;

    public TelegramAuthService(IConfiguration configuration)
    {
        _botToken = configuration["TELEGRAM_BOT_TOKEN"]
                    ?? configuration["Telegram:BotToken"]
                    ?? throw new InvalidOperationException("TELEGRAM_BOT_TOKEN is required.");

        var maxAgeHours = configuration.GetValue("Telegram:MaxAuthAgeHours", 24);
        _maxAge = TimeSpan.FromHours(maxAgeHours);
    }

    public TelegramAuthResult ValidateInitData(string initData)
    {
        if (string.IsNullOrWhiteSpace(initData))
            throw new ApiException(ApiErrorCodes.TelegramAuthInvalid, "initData is required.", 401);

        var fields = ParseQuery(initData);
        if (!fields.TryGetValue("hash", out var hash) || string.IsNullOrWhiteSpace(hash))
            throw new ApiException(ApiErrorCodes.TelegramAuthInvalid, "Missing hash.", 401);

        var dataCheckString = string.Join('\n',
            fields.Where(kv => !string.Equals(kv.Key, "hash", StringComparison.Ordinal))
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key}={kv.Value}"));

        using var secretHmac = new HMACSHA256(Encoding.UTF8.GetBytes("WebAppData"));
        var secretKey = secretHmac.ComputeHash(Encoding.UTF8.GetBytes(_botToken));

        using var dataHmac = new HMACSHA256(secretKey);
        var calculated = Convert.ToHexString(dataHmac.ComputeHash(Encoding.UTF8.GetBytes(dataCheckString)))
            .ToLowerInvariant();

        if (!FixedTimeEqualsHex(calculated, hash.ToLowerInvariant()))
            throw new ApiException(ApiErrorCodes.TelegramAuthInvalid, "Invalid Telegram signature.", 401);

        if (!fields.TryGetValue("auth_date", out var authDateRaw) || !long.TryParse(authDateRaw, out var authDateUnix))
            throw new ApiException(ApiErrorCodes.TelegramAuthInvalid, "Missing auth_date.", 401);

        var authDate = DateTimeOffset.FromUnixTimeSeconds(authDateUnix);
        var now = DateTimeOffset.UtcNow;
        // Reject future auth_date beyond small clock skew; reject stale beyond MaxAuthAgeHours.
        if (authDate - now > TimeSpan.FromMinutes(2))
            throw new ApiException(ApiErrorCodes.TelegramAuthInvalid, "Telegram auth_date is in the future.", 401);
        if (now - authDate > _maxAge)
            throw new ApiException(ApiErrorCodes.TelegramAuthInvalid, "Telegram auth data expired.", 401);

        if (!fields.TryGetValue("user", out var userJson) || string.IsNullOrWhiteSpace(userJson))
            throw new ApiException(ApiErrorCodes.TelegramAuthInvalid, "Missing user payload.", 401);

        using var doc = JsonDocument.Parse(userJson);
        var root = doc.RootElement;
        if (!root.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.Number)
            throw new ApiException(ApiErrorCodes.TelegramAuthInvalid, "Missing Telegram user id.", 401);

        var telegramUserId = idProp.GetInt64();
        var username = root.TryGetProperty("username", out var u) ? u.GetString() : null;
        var first = root.TryGetProperty("first_name", out var f) ? f.GetString() : null;
        var last = root.TryGetProperty("last_name", out var l) ? l.GetString() : null;
        var fullName = string.Join(' ', new[] { first, last }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var lang = root.TryGetProperty("language_code", out var lc) ? lc.GetString() : null;

        return new TelegramAuthResult
        {
            TelegramUserId = telegramUserId,
            Username = username,
            FullName = string.IsNullOrWhiteSpace(fullName) ? null : fullName,
            LanguageCode = lang
        };
    }

    private static Dictionary<string, string> ParseQuery(string initData)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in initData.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = part.IndexOf('=');
            if (idx <= 0) continue;
            var key = Uri.UnescapeDataString(part[..idx]);
            var value = Uri.UnescapeDataString(part[(idx + 1)..]);
            result[key] = value;
        }

        return result;
    }

    private static bool FixedTimeEqualsHex(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }
}
