using System.Text.Json;

namespace ScarAlpha.Infrastructure.Diagnostics;

/// <summary>Temporary NDJSON debug logger for session 660ec2. Do not log secrets.</summary>
public static class AgentDebugLog
{
    private const string SessionId = "660ec2";
    private static readonly object Gate = new();

    public static void Write(string hypothesisId, string location, string message, object? data = null, string runId = "pre-fix")
    {
        try
        {
            var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["sessionId"] = SessionId,
                ["runId"] = runId,
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

                    lock (Gate)
                    {
                        File.AppendAllText(path, payload + Environment.NewLine);
                    }
                }
                catch
                {
                    // try next path
                }
            }
        }
        catch
        {
            // never break the request path
        }
    }

    private static IEnumerable<string> CandidatePaths()
    {
        var env = Environment.GetEnvironmentVariable("SCARALPHA_AGENT_DEBUG_LOG");
        if (!string.IsNullOrWhiteSpace(env))
            yield return env;

        yield return "/home/web/backend/logs/debug-660ec2.log";
        yield return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "logs", "debug-660ec2.log"));
        yield return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "logs", "debug-660ec2.log"));
        yield return @"d:\work\flul_bot\debug-660ec2.log";
        yield return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "debug-660ec2.log"));
    }
}
