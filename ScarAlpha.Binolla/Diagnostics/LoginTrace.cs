using System.Text.Json;

namespace ScarAlpha.Binolla.Diagnostics;

/// <summary>Minimal NDJSON breadcrumbs for Binolla login/WS auth. Never logs secrets.</summary>
internal static class LoginTrace
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
    }
}
