using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;

namespace ScarAlpha.Infrastructure.Binolla;

/// <summary>
/// Runs backend/tools/binolla-auth/capture.mjs — a real Chromium driven through the
/// broker's own sign-in page — to obtain a session token from email/password.
/// Password is never logged or persisted.
///
/// <para>Serves every broker whose session can only be obtained this way. The script
/// knows each venue's URLs and login shape; this class picks the broker, runs it, and
/// converts the captured token into whatever that venue's client expects to send.</para>
/// </summary>
public sealed class NodeBinollaCredentialAuth : IBinollaCredentialAuth
{
    private readonly ILogger<NodeBinollaCredentialAuth> _logger;
    private readonly bool _enabled;
    private readonly bool _headless;
    private readonly string _toolDirectory;
    private readonly string _nodeExecutable;
    /// <summary>Per-broker URL overrides from configuration; empty means "use the script's own".</summary>
    private readonly IConfiguration _configuration;
    private readonly int _timeoutMs;
    private readonly string? _proxyServer;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public NodeBinollaCredentialAuth(
        IConfiguration configuration,
        IHostEnvironment hostEnvironment,
        ILogger<NodeBinollaCredentialAuth> logger)
    {
        _logger = logger;
        _enabled = configuration.GetValue("Binolla:CredentialLogin:Enabled", true);
        _headless = configuration.GetValue("Binolla:CredentialLogin:Headless", true);
        _nodeExecutable = configuration["Binolla:CredentialLogin:NodeExecutable"] ?? "node";
        _configuration = configuration;
        _timeoutMs = Math.Clamp(
            configuration.GetValue("Binolla:CredentialLogin:TimeoutSeconds", 60) * 1000,
            15_000,
            120_000);
        _proxyServer = configuration["BINOLLA_AUTH_PROXY"]
                       ?? configuration["Binolla:CredentialLogin:ProxyServer"];

        var configured = configuration["Binolla:CredentialLogin:ToolDirectory"];
        _toolDirectory = !string.IsNullOrWhiteSpace(configured)
            ? configured
            : ResolveDefaultToolDirectory(hostEnvironment.ContentRootPath);
    }

    public Task<BinollaCapturedSession> LoginAsync(
        string broker,
        string email,
        string password,
        CancellationToken cancellationToken = default)
        => CaptureAsync(Brokers.Normalize(broker), "login", email, password, cancellationToken);

    public Task<BinollaCapturedSession> SignUpAsync(
        string broker,
        string email,
        string password,
        CancellationToken cancellationToken = default)
        => CaptureAsync(Brokers.Normalize(broker), "signup", email, password, cancellationToken);

    private async Task<BinollaCapturedSession> CaptureAsync(
        string broker,
        string mode,
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        if (!_enabled)
        {
            throw new ApiException(
                ApiErrorCodes.BinollaLoginFailed,
                "Binolla credential login is disabled on this server.",
                503);
        }

        ValidateCredentials(email, password);

        var scriptPath = Path.Combine(_toolDirectory, "capture.mjs");
        if (!File.Exists(scriptPath))
        {
            throw new ApiException(
                ApiErrorCodes.BinollaLoginFailed,
                $"Binolla auth tool is missing at '{_toolDirectory}'. Run npm install in backend/tools/binolla-auth.",
                503);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var captured = await RunNodeCaptureAsync(broker, mode, email.Trim(), password, cancellationToken);
            var token = NormalizeSessionToken(captured.Token!);
            return new BinollaCapturedSession(BuildSessionPayload(broker, token), captured.Cookies);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Turns the captured token into the exact thing the venue's client sends.
    ///
    /// <para>Binolla's client transmits the frame verbatim, so it is built here. The
    /// Quotex library builds its own <c>authorization</c> frame around the session value
    /// and decides <c>isDemo</c> from the account the caller asked for — handing it a
    /// pre-built frame would either be double-wrapped or would silently override the
    /// balance choice, which is how a real-money trade gets placed on a demo request.</para>
    /// </summary>
    private static string BuildSessionPayload(string broker, string token)
    {
        if (broker == Brokers.Quotex)
            return token;

        // Escape for embedding inside a JSON string literal in the SSID frame.
        var safe = token.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        return $$"""42["authorization",{"isDemo":true,"token":"{{safe}}"}]""";
    }

    private async Task<CaptureResult> RunNodeCaptureAsync(
        string broker,
        string mode,
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _nodeExecutable,
            WorkingDirectory = _toolDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        // Prefer env vars so the password is not visible in process argv.
        psi.Environment["BINOLLA_AUTH_EMAIL"] = email;
        psi.Environment["BINOLLA_AUTH_PASSWORD"] = password;
        if (!string.IsNullOrWhiteSpace(_proxyServer))
            psi.Environment["BINOLLA_AUTH_PROXY"] = _proxyServer;

        psi.ArgumentList.Add("capture.mjs");
        psi.ArgumentList.Add("--broker");
        psi.ArgumentList.Add(broker);
        psi.ArgumentList.Add("--mode");
        psi.ArgumentList.Add(mode);
        psi.ArgumentList.Add("--headless");
        psi.ArgumentList.Add(_headless ? "true" : "false");
        // Only override the script's own URLs when this deployment has been told to. The
        // defaults live in one table beside the login logic, so a new venue does not need
        // a matching set of appsettings keys before it can be used at all.
        AddUrlOverride(psi, "--loginUrl", broker, "LoginUrl");
        AddUrlOverride(psi, "--signupUrl", broker, "SignupUrl");
        AddUrlOverride(psi, "--tradingUrl", broker, "TradingUrl");
        psi.ArgumentList.Add("--timeoutMs");
        psi.ArgumentList.Add(_timeoutMs.ToString());
        if (!string.IsNullOrWhiteSpace(_proxyServer))
        {
            psi.ArgumentList.Add("--proxy");
            psi.ArgumentList.Add(_proxyServer);
        }

        _logger.LogInformation(
            "Starting {Broker} credential {Mode} capture from {ToolDir}", broker, mode, _toolDirectory);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new ApiException(
                ApiErrorCodes.BinollaLoginFailed,
                "Failed to start Binolla auth tool (node).",
                502);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeoutMs + 15_000);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new ApiException(
                ApiErrorCodes.BinollaLoginFailed,
                "Binolla login timed out.",
                504);
        }

        var stdout = (await stdoutTask).Trim();
        var stderr = (await stderrTask).Trim();

        if (!string.IsNullOrWhiteSpace(stderr))
            _logger.LogDebug("Binolla auth tool stderr length={Length}", stderr.Length);

        if (string.IsNullOrWhiteSpace(stdout))
        {
            throw new ApiException(
                ApiErrorCodes.BinollaLoginFailed,
                "Binolla auth tool returned no output. Run npm install / npx playwright install chromium in backend/tools/binolla-auth.",
                502);
        }

        CaptureResult result;
        try
        {
            result = JsonSerializer.Deserialize<CaptureResult>(stdout, JsonOptions)
                     ?? new CaptureResult(false, null, null, "Invalid tool response");
        }
        catch (JsonException)
        {
            _logger.LogWarning("Binolla auth tool returned non-JSON output");
            throw new ApiException(
                ApiErrorCodes.BinollaLoginFailed,
                "Binolla auth tool returned an invalid response.",
                502);
        }

        if (!result.Ok || string.IsNullOrWhiteSpace(result.Token) || result.Token.Length < 16)
        {
            var safeError = SanitizeCaptureError(result.Error, stderr);
            // A CAPTCHA carries its own code: nothing is wrong with the credentials, and
            // the caller can offer the user the guided login rather than an error.
            var code = ClassifyCaptureError(result.Error, stderr) == "captcha-required"
                ? ApiErrorCodes.BinollaCaptchaRequired
                : ApiErrorCodes.BinollaLoginFailed;
            // Use 400 so clients do not treat this as Scar Alpha JWT expiry.
            throw new ApiException(code, safeError, 400);
        }

        // tokenSource is optional (older capture.mjs may omit it); token field remains required.
        _logger.LogInformation(
            "{Broker} credential {Mode} captured session token (cookiesPresent={HasCookies}, tokenSource={TokenSource}, tokenLen={TokenLen})",
            broker,
            mode,
            !string.IsNullOrWhiteSpace(result.Cookies),
            string.IsNullOrWhiteSpace(result.TokenSource) ? "unspecified" : result.TokenSource,
            result.Token.Length);
        return result;
    }

    /// <summary>
    /// Adds a URL override only if this deployment configured one for this broker.
    ///
    /// <para>Reads <c>Binolla:CredentialLogin:{broker}:{key}</c> first, then the legacy
    /// unprefixed <c>Binolla:CredentialLogin:{key}</c> for Binolla, so existing servers
    /// keep whatever they already set.</para>
    /// </summary>
    private void AddUrlOverride(ProcessStartInfo psi, string flag, string broker, string key)
    {
        var value = _configuration[$"Binolla:CredentialLogin:{broker}:{key}"];
        if (string.IsNullOrWhiteSpace(value) && broker == Brokers.Binolla)
            value = _configuration[$"Binolla:CredentialLogin:{key}"];

        if (string.IsNullOrWhiteSpace(value))
            return;

        psi.ArgumentList.Add(flag);
        psi.ArgumentList.Add(value.Trim());
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // ignore
        }
    }

    private static string ResolveDefaultToolDirectory(string contentRootPath)
    {
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(contentRootPath, "..", "tools", "binolla-auth")),
            Path.GetFullPath(Path.Combine(contentRootPath, "tools", "binolla-auth")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "tools", "binolla-auth")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "tools", "binolla-auth")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "tools", "binolla-auth")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "tools", "binolla-auth"))
        };

        foreach (var path in candidates)
        {
            if (File.Exists(Path.Combine(path, "capture.mjs")))
                return path;
        }

        return candidates[0];
    }

    private static string NormalizeSessionToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ApiException(ApiErrorCodes.BinollaLoginFailed, "Binolla login failed.", 400);

        var s = token.Trim();
        if (s.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(s);
                if (doc.RootElement.TryGetProperty("value", out var value) &&
                    value.ValueKind == JsonValueKind.String)
                {
                    s = value.GetString()?.Trim() ?? s;
                }
                else if (doc.RootElement.TryGetProperty("token", out var nested) &&
                         nested.ValueKind == JsonValueKind.String)
                {
                    s = nested.GetString()?.Trim() ?? s;
                }
            }
            catch (JsonException)
            {
                // keep original
            }
        }

        if (s.Length < 16 || s.Contains('{', StringComparison.Ordinal) || s.Contains('}', StringComparison.Ordinal))
        {
            throw new ApiException(
                ApiErrorCodes.BinollaLoginFailed,
                "Binolla returned an invalid session token shape.",
                400);
        }

        return s;
    }

    private static void ValidateCredentials(string email, string password)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Trim().Length < 3 || !email.Contains('@'))
            throw new ApiException(ApiErrorCodes.ValidationError, "A valid Binolla email is required.");
        if (string.IsNullOrWhiteSpace(password) || password.Length < 4)
            throw new ApiException(ApiErrorCodes.ValidationError, "Binolla password is required.");
        if (password.Length > 128 || email.Length > 256)
            throw new ApiException(ApiErrorCodes.ValidationError, "Credentials exceed allowed length.");
    }

    private static string ClassifyCaptureError(string? error, string? stderr)
    {
        var blob = $"{error}\n{stderr}";
        if (blob.Contains("shared libraries", StringComparison.OrdinalIgnoreCase)
            || blob.Contains("libatk", StringComparison.OrdinalIgnoreCase)
            || blob.Contains("cannot open shared object", StringComparison.OrdinalIgnoreCase))
            return "missing-os-libs";
        if (blob.Contains("not available in your current location", StringComparison.OrdinalIgnoreCase)
            || blob.Contains("United Kingdom", StringComparison.OrdinalIgnoreCase)
            || blob.Contains("(GB)", StringComparison.OrdinalIgnoreCase)
            || blob.Contains("geo-restriction", StringComparison.OrdinalIgnoreCase))
            return "geo-blocked";
        // Binolla asked for a CAPTCHA. This arrives as HTTP 200 with captchaRequired in
        // the body — the request reached the auth logic and was answered, so it is not a
        // block and must not be reported as one. It is also intermittent: the next attempt
        // a minute later usually goes through.
        if (blob.Contains("captchaRequired\":true", StringComparison.OrdinalIgnoreCase)
            || blob.Contains("captcharequired\": true", StringComparison.OrdinalIgnoreCase))
            return "captcha-required";

        // A 403 on Binolla's auth endpoint is an edge/WAF refusal — the request was
        // rejected before the password was ever checked. Wrong credentials come back as
        // 401 with a message, so these two must not read the same to an operator.
        if (blob.Contains("/auth/login:403", StringComparison.OrdinalIgnoreCase)
            || blob.Contains("HTTP 403", StringComparison.OrdinalIgnoreCase))
            return "edge-blocked";
        if (blob.Contains("browserType.launch", StringComparison.OrdinalIgnoreCase))
            return "browser-launch";
        if (blob.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            return "timeout";
        return "other";
    }

    private static string SanitizeCaptureError(string? error, string? stderr = null)
    {
        var kind = ClassifyCaptureError(error, stderr);
        if (kind is "missing-os-libs" or "browser-launch")
        {
            return "Binolla browser failed to start on the server (missing Chromium OS libraries). "
                   + "On the VPS run: cd /home/web/backend/tools/binolla-auth && chmod +x install-deps.sh && ./install-deps.sh";
        }

        if (kind == "captcha-required")
        {
            return "Binolla asked for a human check on this login. Continue in the guided "
                   + "login window to answer it.";
        }

        if (kind == "edge-blocked")
        {
            return "Binolla refused the login from this server (HTTP 403 - blocked before the "
                   + "password was checked). This is an IP/location or bot-protection block, not "
                   + "a wrong password. Set BINOLLA_AUTH_PROXY in scaralpha.env to a proxy in an "
                   + "allowed country, or paste your Binolla SSID from Edit Profile.";
        }

        if (kind == "geo-blocked")
        {
            return "Binolla blocked this server IP by location (geo-restriction). "
                   + "Set BINOLLA_AUTH_PROXY in scaralpha.env to a proxy in an allowed country, "
                   + "or paste your Binolla SSID from Edit Profile.";
        }

        if (string.IsNullOrWhiteSpace(error))
            return "Binolla login failed.";

        var trimmed = error.Trim();
        // Roomy enough to keep the page diagnostics the capture attaches when it cannot
        // find a field. Cutting those off leaves the message saying only that a selector
        // did not match — which is the one thing already known.
        return trimmed.Length > 500 ? trimmed[..500] + "…" : trimmed;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Capture JSON from capture.mjs. <see cref="TokenSource"/> is optional (ws-auth|api|…).
    /// Extra JSON fields are ignored by default System.Text.Json options.
    /// </summary>
    private sealed record CaptureResult(
        bool Ok,
        string? Token,
        string? Cookies,
        string? Error,
        string? TokenSource = null);
}
