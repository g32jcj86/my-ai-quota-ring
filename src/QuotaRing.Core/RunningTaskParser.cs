using System.Text;
using System.Text.Json;

namespace QuotaRing.Core;

public static class RunningTaskParser
{
    public static string? ClaudeSession(string json, IReadOnlySet<int> runningProcessIds)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("status").GetString() != "busy" || !runningProcessIds.Contains(root.GetProperty("pid").GetInt32())) return null;
        if (root.TryGetProperty("name", out var name) && !string.IsNullOrWhiteSpace(name.GetString())) return name.GetString();
        return Path.GetFileName(Path.TrimEndingDirectorySeparator(root.GetProperty("cwd").GetString()!));
    }

    public static string? CodexRollout(string firstLine, string tail)
    {
        string name;
        try
        {
            using var metadata = JsonDocument.Parse(firstLine);
            var root = metadata.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "session_meta"
                || !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object
                || !payload.TryGetProperty("source", out var source)) return null;
            if (source.ValueKind == JsonValueKind.String)
            {
                if (source.GetString() == "exec") return null;
                if (!payload.TryGetProperty("cwd", out var cwd) || cwd.ValueKind != JsonValueKind.String) return null;
                name = "Codex 桌面版：" + Path.GetFileName(Path.TrimEndingDirectorySeparator(cwd.GetString()!));
            }
            else
            {
                if (source.ValueKind != JsonValueKind.Object || !source.TryGetProperty("subagent", out var subagent) || subagent.ValueKind != JsonValueKind.Object
                    || !subagent.TryGetProperty("thread_spawn", out var spawn) || spawn.ValueKind != JsonValueKind.Object
                    || !spawn.TryGetProperty("agent_nickname", out var nickname) || nickname.ValueKind != JsonValueKind.String) return null;
                name = "Codex 子代理：" + nickname.GetString();
            }
        }
        catch (JsonException) { return null; }

        string? lastTaskEvent = null;
        using var reader = new StringReader(tail);
        while (reader.ReadLine() is { } line)
        {
            // A byte-limited tail can start mid-line, and the writer can leave a partial final line.
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
                    || type.GetString() != "event_msg" || !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object
                    || !payload.TryGetProperty("type", out var eventType) || eventType.ValueKind != JsonValueKind.String) continue;
                var value = eventType.GetString();
                if (value is "task_started" or "task_complete" or "turn_aborted") lastTaskEvent = value;
            }
            catch (JsonException) { }
        }
        return lastTaskEvent == "task_started" ? name : null;
    }

    public static string? CodexCommandLine(string commandLine)
    {
        // Windows arguments: backslashes are literal except immediately before a quote.
        var arguments = new List<string>();
        var index = 0;
        while (index < commandLine.Length)
        {
            while (index < commandLine.Length && char.IsWhiteSpace(commandLine[index])) index++;
            if (index == commandLine.Length) break;
            var argument = new StringBuilder();
            var quoted = false;
            while (index < commandLine.Length && (quoted || !char.IsWhiteSpace(commandLine[index])))
            {
                var slashes = 0;
                while (index < commandLine.Length && commandLine[index] == '\\') { slashes++; index++; }
                if (index < commandLine.Length && commandLine[index] == '"')
                {
                    argument.Append('\\', slashes / 2);
                    if (slashes % 2 != 0) argument.Append('"');
                    else if (quoted && index + 1 < commandLine.Length && commandLine[index + 1] == '"') { argument.Append('"'); index++; }
                    else quoted = !quoted;
                    index++;
                }
                else
                {
                    argument.Append('\\', slashes);
                    if (index < commandLine.Length && (quoted || !char.IsWhiteSpace(commandLine[index]))) argument.Append(commandLine[index++]);
                }
            }
            arguments.Add(argument.ToString());
        }
        if (arguments.Count < 2 || !Path.GetFileName(arguments[0]).Equals("codex.exe", StringComparison.OrdinalIgnoreCase) || arguments[1] != "exec") return null;
        for (var argumentIndex = 2; argumentIndex + 1 < arguments.Count; argumentIndex++)
        {
            if (arguments[argumentIndex] == "--") break;
            if (arguments[argumentIndex] == "-C") return Path.GetFileName(Path.TrimEndingDirectorySeparator(arguments[argumentIndex + 1]));
        }
        return "codex.exe exec";
    }
}
