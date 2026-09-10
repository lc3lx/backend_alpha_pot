namespace ScarAlpha.Application.Abstractions;

/// <summary>
/// A Binolla login the user finishes themselves, in a browser we hold open for them.
///
/// <para><b>Why this exists.</b> Binolla answers a login with HTTP 200 and
/// <c>captchaRequired: true</c>. There is no challenge image in that response to show
/// anyone — the widget only exists inside a live browser session on the page — and the
/// one-shot capture closes its browser the moment it sees the flag. The user was
/// therefore told to sign in at binolla.com and paste an SSID, which assumes they know
/// what an SSID is. Most do not.</para>
///
/// <para>So the browser stays open instead: the page is relayed to the user as an image,
/// their clicks and keystrokes are replayed into it, and the session token is captured
/// when the login completes. The person answering the challenge is the account's own
/// owner. Nothing here attempts to solve, predict or bypass a challenge.</para>
/// </summary>
public interface IBinollaInteractiveAuth
{
    /// <summary>
    /// Opens a browser on the login page with the credentials pre-filled and returns the
    /// first frame. Nothing is submitted — the user drives from here.
    /// </summary>
    Task<InteractiveLoginState> StartAsync(
        string email, string password, CancellationToken ct = default);

    /// <summary>Replays one user action into the live page and returns the next frame.</summary>
    Task<InteractiveLoginState> SendEventAsync(
        InteractiveLoginEvent action, CancellationToken ct = default);

    /// <summary>Latest frame and state, without acting on the page.</summary>
    Task<InteractiveLoginState> GetStateAsync(string sessionId, CancellationToken ct = default);

    /// <summary>Closes the browser. Called on success, cancel, or navigation away.</summary>
    Task CloseAsync(string sessionId, CancellationToken ct = default);
}

/// <summary>
/// One relayed user action.
/// </summary>
/// <param name="SessionId">Which login session to act on.</param>
/// <param name="Type">click, move, type, key, scroll, or refresh.</param>
/// <param name="X">Pointer position in page pixels, for pointer events.</param>
/// <param name="Y">Pointer position in page pixels, for pointer events.</param>
/// <param name="Text">Characters to type.</param>
/// <param name="Key">A single named key, e.g. Enter or Backspace.</param>
/// <param name="DeltaY">Wheel movement.</param>
public sealed record InteractiveLoginEvent(
    string SessionId,
    string Type,
    double? X = null,
    double? Y = null,
    string? Text = null,
    string? Key = null,
    double? DeltaY = null);

/// <summary>
/// What the user is looking at, and whether the login is done.
/// </summary>
/// <param name="SessionId">Handle for the next event.</param>
/// <param name="State">starting, awaiting-user, success, or failed.</param>
/// <param name="Screenshot">The live page as a data URI, for display only.</param>
/// <param name="Width">Frame width in page pixels, so clicks can be scaled back.</param>
/// <param name="Height">Frame height in page pixels.</param>
/// <param name="Message">Broker or transport message when the state is failed.</param>
/// <param name="Token">
/// Captured session token, present only on success. Treated exactly like a pasted SSID
/// from here on — encrypted at rest, never logged, never returned to the browser.
/// </param>
/// <param name="CookieHeader">Cookies captured alongside the token. Never logged.</param>
public sealed record InteractiveLoginState(
    string SessionId,
    string State,
    string? Screenshot,
    int Width,
    int Height,
    string? Message = null,
    string? Token = null,
    string? CookieHeader = null)
{
    public bool IsSuccess => string.Equals(State, "success", StringComparison.OrdinalIgnoreCase);

    public bool IsFailed => string.Equals(State, "failed", StringComparison.OrdinalIgnoreCase);
}
