using System.Text.Json;

namespace ScarAlpha.Binolla.Diagnostics;

/// <summary>Minimal NDJSON breadcrumbs for Binolla login/WS auth. Never logs secrets.</summary>
public static class LoginTrace
{
    private const string SessionId = "660ec2";

    public static void Write(string hypothesisId, string location, string message, object? data = null)
    {
        try
        {
            var line = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["sessionId"] = SessionId,
                ["runId"] = "login-fix",
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

            // #region agent log
            // Mirror critical crumbs to stdout so PM2 out log captures them without scp'ing NDJSON.
            try
            {
                var dataJson = data is null ? "{}" : JsonSerializer.Serialize(data);
                if (dataJson.Length > 240)
                    dataJson = dataJson[..240] + "…";
                Console.WriteLine($"DBG660|{hypothesisId}|{message}|{dataJson}");
            }
            catch
            {
                // ignore
            }
            // #endregion
        }
        catch
        {
            // never break trading path
        }
    }

    private static IEnumerable<string> CandidatePaths()
    {
        var cwd = Directory.GetCurrentDirectory();
        yield return "/home/web/backend/logs/debug-660ec2.log";
        yield return Path.GetFullPath(Path.Combine(cwd, "logs", "debug-660ec2.log"));
        yield return Path.GetFullPath(Path.Combine(cwd, "debug-660ec2.log"));
        yield return @"d:\work\flul_bot\debug-660ec2.log";
        yield return @"d:\work\flul_bot\.cursor\debug-660ec2.log";
    }
}
