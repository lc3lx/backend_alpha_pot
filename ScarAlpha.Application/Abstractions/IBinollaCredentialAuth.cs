namespace ScarAlpha.Application.Abstractions;

/// <summary>
/// Drives a real browser through a broker's own sign-in page and keeps the session it
/// produces. Never persists the password.
///
/// <para>This exists because both venues sit behind bot protection that a plain HTTP
/// client cannot pass. Binolla refuses the server outright; Quotex's Cloudflare accepts
/// the polling endpoint but returns 403 on the WebSocket upgrade, so a session opened
/// without a browser dies with <c>Session ID unknown</c> no matter how faithfully the TLS
/// fingerprint is impersonated. A browser that has genuinely logged in produces a session
/// the socket accepts, which is the whole reason for paying its cost.</para>
/// </summary>
public interface IBinollaCredentialAuth
{
    /// <summary>Log into an existing account on <paramref name="broker"/> and return its session.</summary>
    Task<BinollaCapturedSession> LoginAsync(
        string broker,
        string email,
        string password,
        CancellationToken cancellationToken = default);

    /// <summary>Register a new account (partner referral) on <paramref name="broker"/>.</summary>
    Task<BinollaCapturedSession> SignUpAsync(
        string broker,
        string email,
        string password,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A captured browser session. <see cref="CookieHeader"/> must never be logged.
///
/// <para><see cref="SsidFrame"/> is whatever the venue's own client sends to authenticate:
/// Binolla wants the complete <c>42["authorization",…]</c> frame, while the Quotex library
/// builds that frame itself and wants the bare session value. Each broker's capture
/// produces the shape its own consumer expects — see <see cref="Brokers"/>.</para>
/// </summary>
public sealed record BinollaCapturedSession(string SsidFrame, string? CookieHeader);
