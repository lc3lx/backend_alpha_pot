namespace ScarAlpha.Application.Abstractions;

/// <summary>
/// Obtains a Binolla WebSocket auth frame (SSID) from email/password.
/// Based on the unofficial A11ksa/API-Binolla Playwright login flow.
/// Never persists the password.
/// </summary>
public interface IBinollaCredentialAuth
{
    /// <summary>Log into an existing Binolla account and return SSID + optional cookies.</summary>
    Task<BinollaCapturedSession> LoginAsync(string email, string password, CancellationToken cancellationToken = default);

    /// <summary>Register a new Binolla account (partner referral) and return SSID + optional cookies.</summary>
    Task<BinollaCapturedSession> SignUpAsync(string email, string password, CancellationToken cancellationToken = default);
}

/// <summary>Playwright capture result. CookieHeader must never be logged.</summary>
public sealed record BinollaCapturedSession(string SsidFrame, string? CookieHeader);
