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
            var token = await RunNodeCaptureAsync(mode, email.Trim(), password, cancellationToken);
            return $$"""42["authorization",{"isDemo":true,"token":"{{token}}"}]""";
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

        _logger.LogInformation("Starting Binolla credential {Mode} capture from {ToolDir}", mode, _toolDirectory);

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
                     ?? new CaptureResult(false, null, "Invalid tool response");
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
            // Use 400 so clients do not treat this as Scar Alpha JWT expiry.
            throw new ApiException(
                ApiErrorCodes.BinollaLoginFailed,
                string.IsNullOrWhiteSpace(result.Error)
                    ? "Binolla login failed."
                    : result.Error,
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

    private static void ValidateCredentials(string email, string password)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Trim().Length < 3 || !email.Contains('@'))
            throw new ApiException(ApiErrorCodes.ValidationError, "A valid Binolla email is required.");
        if (string.IsNullOrWhiteSpace(password) || password.Length < 4)
            throw new ApiException(ApiErrorCodes.ValidationError, "Binolla password is required.");
        if (password.Length > 128 || email.Length > 256)
            throw new ApiException(ApiErrorCodes.ValidationError, "Credentials exceed allowed length.");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record CaptureResult(bool Ok, string? Token, string? Error);
}
