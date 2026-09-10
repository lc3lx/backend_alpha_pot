using Microsoft.Extensions.Logging;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Application.Contracts;

namespace ScarAlpha.Application.Services;

/// <summary>
/// Guided login: the user solves Binolla's CAPTCHA themselves, in a browser the server
/// holds open on their behalf.
///
/// <para>This is the answer to a real dead end. Binolla returns <c>captchaRequired</c> as a
/// flag on an API response — there is no picture to show — and the automated capture has
/// already closed its browser by the time anyone sees the failure. The previous advice was
/// "sign in at binolla.com and paste your SSID", which is fine for an operator and useless
/// for a user who has never heard of an SSID.</para>
///
/// <para>The credentials are held only for the life of one login and never written down
/// here; the captured token is completed through exactly the same path as an automated
/// capture, so approval, encryption and session handling are unchanged.</para>
/// </summary>
public sealed class BinollaGuidedLoginService
{
    private readonly ICurrentUser _currentUser;
    private readonly IBinollaInteractiveAuth _interactive;
    private readonly BinollaAppService _binolla;
    private readonly IBinollaSessionRestorer _restorer;
    private readonly ILogger<BinollaGuidedLoginService> _logger;

    /// <summary>
    /// Credentials for in-flight guided logins, keyed by session.
    ///
    /// <para>Held in memory only, for the minute or two the user spends on the challenge,
    /// and dropped the moment the login ends either way. They are needed because the
    /// completion step re-connects with the captured token and records the account the
    /// same way the automated path does.</para>
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, PendingLogin>
        Pending = new();

    public BinollaGuidedLoginService(
        ICurrentUser currentUser,
        IBinollaInteractiveAuth interactive,
        BinollaAppService binolla,
        IBinollaSessionRestorer restorer,
        ILogger<BinollaGuidedLoginService> logger)
    {
        _currentUser = currentUser;
        _interactive = interactive;
        _binolla = binolla;
        _restorer = restorer;
        _logger = logger;
    }

    public async Task<GuidedLoginResponse> StartAsync(
        BinollaCredentialRequest request, CancellationToken ct)
    {
        if (request is null)
            throw new ApiException(ApiErrorCodes.ValidationError, "Request body is required.");
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            throw new ApiException(ApiErrorCodes.ValidationError, "Email and password are required.");

        var userId = _currentUser.UserId;
        var state = await _interactive
            .StartAsync(request.Email.Trim(), request.Password, ct)
            .ConfigureAwait(false);

        Pending[state.SessionId] = new PendingLogin(userId, request);
        _logger.LogInformation("Guided login started for user {UserId}", userId);

        return await ToResponseAsync(state, ct).ConfigureAwait(false);
    }

    public async Task<GuidedLoginResponse> SendEventAsync(
        GuidedLoginEventRequest request, CancellationToken ct)
    {
        var pending = Require(request?.SessionId);
        var state = await _interactive
            .SendEventAsync(
                new InteractiveLoginEvent(
                    pending.Key,
                    request!.Type ?? "refresh",
                    request.X,
                    request.Y,
                    request.Text,
                    request.Key,
                    request.DeltaY),
                ct)
            .ConfigureAwait(false);

        return await ToResponseAsync(state, ct).ConfigureAwait(false);
    }

    public async Task<GuidedLoginResponse> GetStateAsync(string? sessionId, CancellationToken ct)
    {
        var pending = Require(sessionId);
        var state = await _interactive.GetStateAsync(pending.Key, ct).ConfigureAwait(false);
        return await ToResponseAsync(state, ct).ConfigureAwait(false);
    }

    public async Task CancelAsync(string? sessionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        Pending.TryRemove(sessionId, out _);
        await _interactive.CloseAsync(sessionId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Turns one relay result into a client response, finishing the connection when the
    /// login has actually succeeded.
    /// </summary>
    private async Task<GuidedLoginResponse> ToResponseAsync(
        InteractiveLoginState state, CancellationToken ct)
    {
        if (!state.IsSuccess)
        {
            if (state.IsFailed) Pending.TryRemove(state.SessionId, out _);

            return new GuidedLoginResponse(
                SessionId: state.SessionId,
                State: state.State,
                Screenshot: state.Screenshot,
                Width: state.Width,
                Height: state.Height,
                Message: state.Message,
                Connection: null);
        }

        if (!Pending.TryRemove(state.SessionId, out var pending))
        {
            throw new ApiException(
                ApiErrorCodes.BinollaLoginFailed,
                "This guided login expired before it finished. Start it again.",
                410);
        }

        if (string.IsNullOrWhiteSpace(state.Token))
        {
            throw new ApiException(
                ApiErrorCodes.BinollaLoginFailed,
                "Binolla accepted the login but returned no session token. Try again.",
                502);
        }

        // The browser has done its job; free it before the slower completion work so a
        // Chromium instance is not held open through a database write.
        await _interactive.CloseAsync(state.SessionId, CancellationToken.None).ConfigureAwait(false);

        // Same completion as an automated capture — approval rules, encryption at rest and
        // session handling are shared rather than reimplemented for this path.
        var captured = new BinollaCapturedSession(state.Token!, state.CookieHeader);
        var connection = await _binolla
            .CompleteCredentialConnectAsync(pending.UserId, captured, pending.Request, ct)
            .ConfigureAwait(false);

        // The user just proved the credentials are good, so any backoff from the failed
        // automatic attempts that led here is stale.
        _restorer.ClearAuthFailure(pending.UserId);
        _logger.LogInformation("Guided login completed for user {UserId}", pending.UserId);

        return new GuidedLoginResponse(
            SessionId: state.SessionId,
            State: "success",
            Screenshot: state.Screenshot,
            Width: state.Width,
            Height: state.Height,
            Message: null,
            Connection: connection);
    }

    private PendingLogin Require(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || !Pending.TryGetValue(sessionId, out var pending))
        {
            throw new ApiException(
                ApiErrorCodes.BinollaLoginFailed,
                "This guided login expired. Start it again.",
                410);
        }

        // A session belongs to the user who opened it. Without this check, knowing a
        // session id would be enough to attach someone else's broker account.
        if (pending.UserId != _currentUser.UserId)
            throw new ApiException(ApiErrorCodes.Forbidden, "Not your login session.", 403);

        return pending with { Key = sessionId! };
    }

    private sealed record PendingLogin(Guid UserId, BinollaCredentialRequest Request)
    {
        public string Key { get; init; } = string.Empty;
    }
}
