using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScarAlpha.Application.Abstractions;
using ScarAlpha.Application.Common;
using ScarAlpha.Infrastructure.Diagnostics;

namespace ScarAlpha.Infrastructure.Binolla;

/// <summary>
/// Runs backend/tools/binolla-auth/capture.mjs (A11ksa/API-Binolla login.py port)
/// to obtain a Binolla session token from email/password, then builds an SSID frame.
/// Password is never logged or persisted.
/// </summary>
public sealed class NodeBinollaCredentialAuth : IBinollaCredentialAuth
{
    private readonly ILogger<NodeBinollaCredentialAuth> _logger;
    private readonly bool _enabled;
    private readonly bool _headless;
    private readonly string _toolDirectory;
    private readonly string _nodeExecutable;
    private readonly string _loginUrl;
    private readonly string _signupUrl;
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
        _loginUrl = configuration["Binolla:CredentialLogin:LoginUrl"] ?? "https://binolla.com/login/";
        _signupUrl = configuration["Binolla:CredentialLogin:SignupUrl"]
                     ?? "https://binolla.com/signup/?lid=15968";
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

    public Task<string> LoginAsync(string email, string password, CancellationToken cancellationToken = default)
        => CaptureAsync("login", email, password, cancellationToken);

    public Task<string> SignUpAsync(string email, string password, CancellationToken cancellationToken = default)
        => CaptureAsync("signup", email, password, cancellationToken);

    private async Task<string> CaptureAsync(
        string mode,
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        // #region agent log
        AgentDebugLog.Write("E", "NodeBinollaCredentialAuth.CaptureAsync:entry", "credential capture start", new
        {
            mode,
            enabled = _enabled,
            toolDirectory = _toolDirectory,
            scriptExists = File.Exists(Path.Combine(_toolDirectory, "capture.mjs")),
            nodeModulesExists = Directory.Exists(Path.Combine(_toolDirectory, "node_modules")),
            playwrightExists = Directory.Exists(Path.Combine(_toolDirectory, "node_modules", "playwright")),
            headless = _headless,
            timeoutMs = _timeoutMs,
            hasProxy = !string.IsNullOrWhiteSpace(_proxyServer),
            emailLen = email?.Length ?? 0,
            contentRootHint = Directory.GetCurrentDirectory()
        });
        // #endregion

        if (!_enabled)
        {
            // #region agent log
            AgentDebugLog.Write("E", "NodeBinollaCredentialAuth.CaptureAsync:disabled", "credential login disabled");
            // #endregion
            throw new ApiException(
                ApiErrorCodes.BinollaLoginFailed,
                "Binolla credential login is disabled on this server.",
                503);
        }

        ValidateCredentials(email, password);

        var scriptPath = Path.Combine(_toolDirectory, "capture.mjs");
        if (!File.Exists(scriptPath))
        {
            // #region agent log
            AgentDebugLog.Write("E", "NodeBinollaCredentialAuth.CaptureAsync:missingTool", "capture.mjs missing", new
            {
                toolDirectory = _toolDirectory,
                scriptPath
            });
            // #endregion
            throw new ApiException(
                ApiErrorCodes.BinollaLoginFailed,
                $"Binolla auth tool is missing at '{_toolDirectory}'. Run npm install in backend/tools/binolla-auth.",
                503);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var token = await RunNodeCaptureAsync(mode, email.Trim(), password, cancellationToken);
            token = NormalizeSessionToken(token);
            // #region agent log
            AgentDebugLog.Write("A", "NodeBinollaCredentialAuth.CaptureAsync:success", "token captured", new
            {
                mode,
                tokenLen = token.Length,
                looksLikeUuid = System.Text.RegularExpressions.Regex.IsMatch(
                    token,
                    @"^[0-9a-fA-F-]{36}$")
            });
            // #endregion
            // Escape for embedding inside a JSON string literal in the SSID frame.
            var safe = token.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal);
            return $$"""42["authorization",{"isDemo":true,"token":"{{safe}}"}]""";
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> RunNodeCaptureAsync(
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
        psi.Environment["SCARALPHA_AGENT_DEBUG_LOG"] =
            Environment.GetEnvironmentVariable("SCARALPHA_AGENT_DEBUG_LOG")
            ?? "/home/web/backend/logs/debug-660ec2.log";
        if (!string.IsNullOrWhiteSpace(_proxyServer))
            psi.Environment["BINOLLA_AUTH_PROXY"] = _proxyServer;

        psi.ArgumentList.Add("capture.mjs");
        psi.ArgumentList.Add("--mode");
        psi.ArgumentList.Add(mode);
        psi.ArgumentList.Add("--headless");
        psi.ArgumentList.Add(_headless ? "true" : "false");
        psi.ArgumentList.Add("--loginUrl");
        psi.ArgumentList.Add(_loginUrl);
        psi.ArgumentList.Add("--signupUrl");
        psi.ArgumentList.Add(_signupUrl);
        psi.ArgumentList.Add("--timeoutMs");
        psi.ArgumentList.Add(_timeoutMs.ToString());
        if (!string.IsNullOrWhiteSpace(_proxyServer))
        {
            psi.ArgumentList.Add("--proxy");
            psi.ArgumentList.Add(_proxyServer);
        }

        _logger.LogInformation("Starting Binolla credential {Mode} capture from {ToolDir}", mode, _toolDirectory);
        // #region agent log
        AgentDebugLog.Write("A", "NodeBinollaCredentialAuth.RunNodeCaptureAsync:start", "spawning node capture", new
        {
            mode,
            toolDirectory = _toolDirectory,
            nodeExecutable = _nodeExecutable,
            timeoutMs = _timeoutMs,
            hasProxy = !string.IsNullOrWhiteSpace(_proxyServer)
        });
        // #endregion

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
            // #region agent log
            AgentDebugLog.Write("D", "NodeBinollaCredentialAuth.RunNodeCaptureAsync:timeout", "process wait timed out", new
            {
                mode,
                timeoutMs = _timeoutMs
            });
            // #endregion
            throw new ApiException(
                ApiErrorCodes.BinollaLoginFailed,
                "Binolla login timed out.",
                504);
        }

        var stdout = (await stdoutTask).Trim();
        var stderr = (await stderrTask).Trim();

        if (!string.IsNullOrWhiteSpace(stderr))
            _logger.LogDebug("Binolla auth tool stderr length={Length}", stderr.Length);

        // #region agent log
        AgentDebugLog.Write("A", "NodeBinollaCredentialAuth.RunNodeCaptureAsync:exit", "node capture finished", new
        {
            mode,
            exitCode = process.HasExited ? process.ExitCode : (int?)null,
            stdoutLen = stdout.Length,
            stderrLen = stderr.Length,
            stderrTail = stderr.Length > 400 ? stderr[^400..] : stderr,
            stdoutPreview = stdout.Length > 180 ? stdout[..180] : stdout
        });
        // #endregion

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
                     ?? new CaptureResult(false, null, "Invalid tool response");
        }
        catch (JsonException)
        {
            _logger.LogWarning("Binolla auth tool returned non-JSON output");
            // #region agent log
            AgentDebugLog.Write("A", "NodeBinollaCredentialAuth.RunNodeCaptureAsync:badJson", "non-JSON stdout");
            // #endregion
            throw new ApiException(
                ApiErrorCodes.BinollaLoginFailed,
                "Binolla auth tool returned an invalid response.",
                502);
        }

        if (!result.Ok || string.IsNullOrWhiteSpace(result.Token) || result.Token.Length < 16)
        {
            var safeError = SanitizeCaptureError(result.Error, stderr);
            // #region agent log
            AgentDebugLog.Write("A", "NodeBinollaCredentialAuth.RunNodeCaptureAsync:fail", "capture returned error", new
            {
                mode,
                ok = result.Ok,
                error = safeError,
                rawErrorKind = ClassifyCaptureError(result.Error, stderr),
                hasToken = !string.IsNullOrWhiteSpace(result.Token)
            });
            // #endregion
            // Use 400 so clients do not treat this as Scar Alpha JWT expiry.
            throw new ApiException(
                ApiErrorCodes.BinollaLoginFailed,
                safeError,
                400);
        }

        _logger.LogInformation("Binolla credential {Mode} captured session token", mode);
        return result.Token;
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

        if (kind == "geo-blocked")
        {
            return "Binolla blocked this server IP by location (geo-restriction). "
                   + "Set BINOLLA_AUTH_PROXY in scaralpha.env to a proxy in an allowed country, "
                   + "or paste your Binolla SSID from Edit Profile.";
        }

        if (string.IsNullOrWhiteSpace(error))
            return "Binolla login failed.";

        var trimmed = error.Trim();
        return trimmed.Length > 240 ? trimmed[..240] + "…" : trimmed;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record CaptureResult(bool Ok, string? Token, string? Error);
}
