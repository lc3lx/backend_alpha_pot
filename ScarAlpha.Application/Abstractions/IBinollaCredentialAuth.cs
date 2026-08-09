namespace ScarAlpha.Application.Abstractions;

/// <summary>
/// Obtains a Binolla WebSocket auth frame (SSID) from email/password.
/// Based on the unofficial A11ksa/API-Binolla Playwright login flow.
/// Never persists the password.
/// </summary>
public interface IBinollaCredentialAuth
{
    /// <summary>Log into an existing Binolla account and return a full SSID frame.</summary>
    Task<string> LoginAsync(string email, string password, CancellationToken cancellationToken = default);

    /// <summary>Register a new Binolla account (partner referral) and return a full SSID frame.</summary>
    Task<string> SignUpAsync(string email, string password, CancellationToken cancellationToken = default);
}
