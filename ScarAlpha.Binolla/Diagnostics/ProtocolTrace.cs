using System.Text.Json;

namespace ScarAlpha.Binolla.Diagnostics;

/// <summary>
/// Safe NDJSON protocol breadcrumbs for Binolla WS debugging. Never logs tokens/cookies/passwords.
/// </summary>
internal static class ProtocolTrace
{
    private const string SessionId = "660ec2";

    public static void Write(string hypothesisId, string location, string message, object? data = null)
    {
        try
        {
            var line = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["sessionId"] = SessionId,
                ["runId"] = "ws-auth",
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

    public static string SafePrefix(string? raw, int max = 48)
    {
        if (string.IsNullOrEmpty(raw))
            return "<empty>";
        var s = raw.Length <= max ? raw : raw[..max];
        if (s.Contains("token", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("authorization", StringComparison.OrdinalIgnoreCase) && s.Contains('{'))
            return $"len={raw.Length};kind={Classify(raw)}";
        return $"len={raw.Length};prefix={s}";
    }

    public static string Classify(string message)
    {
        if (message.Length == 0) return "empty";
        if (message == "2") return "ping";
        if (message == "3") return "pong";
        if (message.StartsWith('0')) return "engine_open";
        if (message.StartsWith("40")) return "ns_connect";
        if (message.StartsWith("41")) return "ns_disconnect";
        if (message.StartsWith("42") && message.Contains("NotAuthorized", StringComparison.Ordinal))
            return "not_authorized";
        if (message.StartsWith("42") && message.Contains("s_authorization", StringComparison.Ordinal))
            return "s_authorization";
        if (message.StartsWith("42"))
        {
            var m = System.Text.RegularExpressions.Regex.Match(message, "^42\\[\"([^\"]+)\"");
            return m.Success ? $"event:{m.Groups[1].Value}" : "event";
        }
        if (message.StartsWith("451-[")) return "binary_header";
        return "other";
    }

    private static IEnumerable<string> CandidatePaths()
    {
        yield return "/home/web/backend/logs/debug-660ec2.log";
        yield return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "logs", "debug-660ec2.log"));
        yield return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "logs", "debug-660ec2.log"));
        yield return @"d:\work\flul_bot\debug-660ec2.log";
    }
}
