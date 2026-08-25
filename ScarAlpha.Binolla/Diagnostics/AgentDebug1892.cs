using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ScarAlpha.Binolla.Diagnostics;

/// <summary>
/// Optional debug NDJSON for session 1892a4. Never logs secrets.
/// Disabled by default — set SCARALPHA_AGENT_DEBUG=1 to enable.
/// </summary>
public static class AgentDebug1892
{
    private const string SessionId = "1892a4";
    private const string IngestUrl = "http://127.0.0.1:7892/ingest/aea6d51e-f3e9-4c7e-b6b4-db55c4306e97";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(400) };

    private static readonly bool Enabled =
        string.Equals(
            Environment.GetEnvironmentVariable("SCARALPHA_AGENT_DEBUG"),
            "1",
            StringComparison.Ordinal);

    public static void Write(
        string hypothesisId,
        string location,
        string message,
        object? data = null,
        string runId = "entry-lag")
    {
        if (!Enabled)
            return;

        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["sessionId"] = SessionId,
                ["runId"] = runId,
                ["hypothesisId"] = hypothesisId,
                ["location"] = location,
                ["message"] = message,
                ["data"] = data,
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            var line = JsonSerializer.Serialize(payload);

            foreach (var path in CandidatePaths())
            {
                try
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);
                    File.AppendAllText(path, line + Environment.NewLine);
                }
                catch
                {
                    // try next
                }
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    using var content = new StringContent(line, Encoding.UTF8, "application/json");
                    using var req = new HttpRequestMessage(HttpMethod.Post, IngestUrl) { Content = content };
                    req.Headers.TryAddWithoutValidation("X-Debug-Session-Id", SessionId);
                    await Http.SendAsync(req).ConfigureAwait(false);
                }
                catch
                {
                    // local ingest may be unreachable on VPS
                }
            });

            try
            {
                var dataJson = data is null ? "{}" : JsonSerializer.Serialize(data);
                if (dataJson.Length > 280)
                    dataJson = dataJson[..280] + "…";
                Console.WriteLine($"DBG1892|{hypothesisId}|{message}|{dataJson}");
            }
            catch
            {
                // ignore
            }
        }
        catch
        {
            // never break trading
        }
    }

    private static IEnumerable<string> CandidatePaths()
    {
        var cwd = Directory.GetCurrentDirectory();
        yield return Path.GetFullPath(Path.Combine(cwd, "debug-1892a4.log"));
        yield return Path.GetFullPath(Path.Combine(cwd, "logs", "debug-1892a4.log"));
        yield return "/home/web/backend/logs/debug-1892a4.log";
        yield return @"d:\work\flul_bot\debug-1892a4.log";
        yield return @"d:\work\flul_bot\.cursor\debug-1892a4.log";
    }
}
