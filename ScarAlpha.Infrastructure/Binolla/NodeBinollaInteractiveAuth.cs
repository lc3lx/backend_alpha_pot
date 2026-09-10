using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;

namespace ScarAlpha.Infrastructure.Binolla;

/// <summary>
/// Talks to the Node interactive-login server (<c>tools/binolla-auth/interactive-server.mjs</c>).
///
/// <para>That server is a separate process because it holds a Chromium instance open per
/// login — the one-shot capture cannot, since it exits before the user ever sees the
/// challenge. This class is the only place in .NET aware of it.</para>
/// </summary>
public sealed class NodeBinollaInteractiveAuth : IBinollaInteractiveAuth
{
    public const string HttpClientName = "binolla-interactive";
    public const string DefaultBaseUrl = "http://127.0.0.1:8110";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<NodeBinollaInteractiveAuth> _logger;

    public NodeBinollaInteractiveAuth(
        IHttpClientFactory httpFactory,
        ILogger<NodeBinollaInteractiveAuth> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<InteractiveLoginState> StartAsync(
        string email, string password, CancellationToken ct = default)
    {
        // Launching a browser and loading the login page through a proxy is slow; the
        // budget has to cover it or the user gets an error while it is still working.
        var dto = await PostAsync<SessionDto>(
            "/session/start",
            new { email, password },
            TimeSpan.FromSeconds(90),
            ct).ConfigureAwait(false);

        return Map(dto);
    }

    public async Task<InteractiveLoginState> SendEventAsync(
        InteractiveLoginEvent action, CancellationToken ct = default)
    {
        var dto = await PostAsync<SessionDto>(
            "/session/event",
            new
            {
                sessionId = action.SessionId,
                type = action.Type,
                x = action.X,
                y = action.Y,
                text = action.Text,
                key = action.Key,
                deltaY = action.DeltaY
            },
            TimeSpan.FromSeconds(30),
            ct).ConfigureAwait(false);

        return Map(dto);
    }

    public async Task<InteractiveLoginState> GetStateAsync(
        string sessionId, CancellationToken ct = default)
    {
        var http = _httpFactory.CreateClient(HttpClientName);
        using var deadline = Deadline(ct, TimeSpan.FromSeconds(30));

        HttpResponseMessage response;
        try
        {
            response = await http
                .GetAsync($"/session/state?sessionId={Uri.EscapeDataString(sessionId)}", deadline.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            throw Unreachable(ex, ct);
        }

        await EnsureOkAsync(response, ct).ConfigureAwait(false);
        var dto = await response.Content.ReadFromJsonAsync<SessionDto>(Json, ct).ConfigureAwait(false);
        return Map(dto);
    }

    public async Task CloseAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            await PostAsync<object>(
                "/session/close",
                new { sessionId },
                TimeSpan.FromSeconds(15),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Closing is best effort: the server expires abandoned sessions on its own,
            // so a failure here must never surface to a user who has already finished.
            _logger.LogDebug(ex, "Interactive login close failed for {SessionId}", sessionId);
        }
    }

    private async Task<T?> PostAsync<T>(
        string path, object body, TimeSpan budget, CancellationToken ct)
    {
        var http = _httpFactory.CreateClient(HttpClientName);
        using var deadline = Deadline(ct, budget);

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync(path, body, Json, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            throw Unreachable(ex, ct);
        }

        await EnsureOkAsync(response, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false);
    }

    private static CancellationTokenSource Deadline(CancellationToken ct, TimeSpan budget)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(budget);
        return cts;
    }

    private ApiException Unreachable(Exception ex, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return new ApiException(ApiErrorCodes.BinollaLoginFailed, "Login cancelled.", 499);

        _logger.LogError(ex, "Interactive login server unreachable");
        return new ApiException(
            ApiErrorCodes.BinollaLoginFailed,
            "The guided-login service is not running on the server. Start it with: "
            + "cd backend/tools/binolla-auth && ./start-interactive-pm2.sh",
            503);
    }

    private static async Task EnsureOkAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var detail = string.Empty;
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            detail = body.Length > 200 ? body[..200] : body;
        }
        catch
        {
            /* status alone is enough */
        }

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new ApiException(
                ApiErrorCodes.BinollaLoginFailed,
                "This guided login expired. Start it again.",
                410);
        }

        throw new ApiException(
            ApiErrorCodes.BinollaLoginFailed,
            $"Guided login failed ({(int)response.StatusCode}). {detail}".Trim(),
            502);
    }

    private static InteractiveLoginState Map(SessionDto? dto)
    {
        if (dto is null)
        {
            throw new ApiException(
                ApiErrorCodes.BinollaLoginFailed, "Guided login returned no state.", 502);
        }

        return new InteractiveLoginState(
            SessionId: dto.SessionId,
            State: dto.State,
            Screenshot: dto.Screenshot,
            Width: dto.Viewport?.Width ?? 0,
            Height: dto.Viewport?.Height ?? 0,
            Message: dto.Message,
            Token: dto.Token,
            CookieHeader: dto.Cookies);
    }

    private sealed record SessionDto(
        [property: JsonPropertyName("sessionId")] string SessionId,
        string State,
        string? Screenshot,
        ViewportDto? Viewport,
        string? Message,
        string? Token,
        string? Cookies);

    private sealed record ViewportDto(int Width, int Height);
}
