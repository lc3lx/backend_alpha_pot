using System.Text.Json;

namespace ScarAlpha.Binolla.Diagnostics;

/// <summary>Debug-mode NDJSON for session 1892a4. Never logs secrets.</summary>
public static class AgentDebug1892
{
    private const string SessionId = "1892a4";

    public static void Write(string hypothesisId, string location, string message, object? data = null)
    {
        // #region agent log
        try
        {
            var line = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["sessionId"] = SessionId,
                ["runId"] = "trade-settle",
                ["hypothesisId"] = hypothesisId,
                ["location"] = location,
                ["message"] = message,
                ["data"] = data,
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });

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
        // #endregion
    }

    private static IEnumerable<string> CandidatePaths()
    {
        var cwd = Directory.GetCurrentDirectory();
        yield return Path.GetFullPath(Path.Combine(cwd, "debug-1892a4.log"));
        yield return Path.GetFullPath(Path.Combine(cwd, "logs", "debug-1892a4.log"));
        yield return "/home/web/backend/logs/debug-1892a4.log";
        yield return @"d:\work\flul_bot\debug-1892a4.log";
    }
}
