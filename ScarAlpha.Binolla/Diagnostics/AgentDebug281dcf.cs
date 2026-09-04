using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ScarAlpha.Binolla.Diagnostics;

/// <summary>
/// Debug NDJSON for session 281dcf. Never logs secrets / plaintext passwords.
/// </summary>
public static class AgentDebug281dcf
{
    private const string SessionId = "281dcf";
    private const string IngestUrl = "http://127.0.0.1:7892/ingest/aea6d51e-f3e9-4c7e-b6b4-db55c4306e97";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(400) };

    public static void Write(
        string hypothesisId,
        string location,
        string message,
        object? data = null,
        string runId = "pre-fix")
    {
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
                    // ingest may be unreachable
                }
            });
        }
        catch
        {
            // never break request path
        }
    }

    private static IEnumerable<string> CandidatePaths()
    {
        var cwd = Directory.GetCurrentDirectory();
        yield return Path.GetFullPath(Path.Combine(cwd, "debug-281dcf.log"));
        yield return Path.GetFullPath(Path.Combine(cwd, "logs", "debug-281dcf.log"));
        yield return @"d:\work\flul_bot\debug-281dcf.log";
        yield return @"d:\work\flul_bot\.cursor\debug-281dcf.log";
    }
}
