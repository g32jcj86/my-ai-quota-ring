using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace QuotaRing.Core;

public sealed record CodexAccount(string Name, string? Email, string? Plan, bool IsActive, bool HasCredentials);

public sealed class CodexAccountStore(string storeDirectory, string authPath)
{
    public string? Active
    {
        get => File.Exists(Path.Combine(storeDirectory, "active.txt")) ? File.ReadAllText(Path.Combine(storeDirectory, "active.txt")) : null;
        set
        {
            Directory.CreateDirectory(storeDirectory);
            File.WriteAllText(Path.Combine(storeDirectory, "active.txt"), value, new UTF8Encoding(false));
        }
    }

    public bool HasCredentials => File.Exists(authPath);
    public string? CurrentEmail => ReadIdentity(authPath).Email;

    public IReadOnlyList<CodexAccount> List()
    {
        var active = Active;
        var names = Directory.Exists(storeDirectory) ? Directory.GetDirectories(storeDirectory)
            .Select(Path.GetFileName).Where(name => !name!.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) : [];
        if (active is not null) names = names.Append(active);
        return names.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Select(name =>
        {
            var isActive = string.Equals(name, active, StringComparison.OrdinalIgnoreCase);
            var path = isActive ? authPath : Path.Combine(storeDirectory, name!, "auth.json");
            var identity = ReadIdentity(path);
            return new CodexAccount(name!, identity.Email, identity.Plan, isActive, File.Exists(path));
        }).ToArray();
    }

    private static (string? Email, string? Plan) ReadIdentity(string path)
    {
        if (!File.Exists(path)) return (null, null);
        var token = JsonNode.Parse(File.ReadAllText(path))?["tokens"]?["id_token"]?.GetValue<string>();
        if (token is null) return (null, null);
        var parts = token.Split('.');
        if (parts.Length != 3) throw new FormatException("Invalid ID token");
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
        var claims = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
        return (claims?["email"]?.GetValue<string>(), claims?["https://api.openai.com/auth"]?["chatgpt_plan_type"]?.GetValue<string>());
    }

    public void Save(string name)
    {
        var directory = Path.Combine(storeDirectory, name);
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "auth.json");
        var temporary = target + ".tmp";
        try
        {
            if (HasCredentials)
            {
                File.Copy(authPath, temporary, true);
                File.Move(temporary, target, true);
            }
            else File.Delete(target);
            Active = name;
        }
        finally { File.Delete(temporary); }
    }

    public void Restore(string name)
    {
        var source = Path.Combine(storeDirectory, name, "auth.json");
        if (File.Exists(source))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(authPath)!);
            File.Copy(source, authPath, true);
        }
        else Clear();
        Active = name;
    }

    public void Clear() => File.Delete(authPath);

    public async Task WaitForAuthAsync(TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        while (HasCredentials)
        {
            try
            {
                using var stream = new FileStream(authPath, FileMode.Open, FileAccess.Read, FileShare.None);
                return;
            }
            catch (IOException)
            {
                if (elapsed.Elapsed >= timeout) throw new IOException("Codex 登入檔案仍被占用，請稍後再試");
            }
            await Task.Delay(300);
        }
    }
}
