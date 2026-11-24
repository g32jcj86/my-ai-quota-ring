using System.Text;
using System.Text.Json.Nodes;
using System.Diagnostics;

namespace QuotaRing.Core;

public sealed record ClaudeAccount(string Name, string? Email, bool IsActive, bool HasCredentials = false);

public sealed class ClaudeAccountStore(string storeDirectory, string desktopDirectory, string credentialsPath, string claudePath)
{
    private static readonly string[] AuthKeys = ["oauth:tokenCache", "oauth:tokenCacheV2", "lastKnownAccountUuid", "planUsageLastTrayOpenAt"];
    private static readonly string[] LoginPaths = ["Network/Cookies", "Network/Cookies-journal", "Local Storage", "IndexedDB", "Session Storage", "plan-usage-history.json"];
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    public string? Active
    {
        get => File.Exists(Path.Combine(storeDirectory, "active.txt")) ? File.ReadAllText(Path.Combine(storeDirectory, "active.txt")) : null;
        set
        {
            Directory.CreateDirectory(storeDirectory);
            File.WriteAllText(Path.Combine(storeDirectory, "active.txt"), value, Utf8);
        }
    }

    public bool HasCredentials => File.Exists(credentialsPath);
    public string? CurrentEmail => HasCredentials ? ReadObject(claudePath)?["oauthAccount"]?["emailAddress"]?.GetValue<string>() : null;
    public string? DesktopAccountUuid => ReadObject(Path.Combine(desktopDirectory, "config.json"))?["lastKnownAccountUuid"]?.GetValue<string>();
    public string? CliAccountUuid => HasCredentials ? ReadObject(claudePath)?["oauthAccount"]?["accountUuid"]?.GetValue<string>() : null;
    public bool IsMismatched
    {
        get
        {
            var desktop = DesktopAccountUuid;
            var cli = CliAccountUuid;
            return desktop is not null && cli is not null && desktop != cli;
        }
    }

    public IReadOnlyList<ClaudeAccount> List()
    {
        var active = Active;
        var names = Directory.Exists(storeDirectory) ? Directory.GetDirectories(storeDirectory)
            .Select(path => Path.GetFileName(path)).Where(name => !name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) : [];
        if (active is not null) names = names.Append(active);
        return names.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
            .Select(name =>
            {
                var isActive = string.Equals(name, active, StringComparison.OrdinalIgnoreCase);
                var hasCredentials = isActive ? HasCredentials : File.Exists(Path.Combine(storeDirectory, name, "credentials.json"));
                var email = hasCredentials ? (isActive ? CurrentEmail : ReadObject(Path.Combine(storeDirectory, name, "oauthAccount.json"))?["emailAddress"]?.GetValue<string>()) : null;
                return new ClaudeAccount(name, email, isActive, hasCredentials);
            }).ToArray();
    }

    public static string? ValidateName(string name, IEnumerable<string> existing)
    {
        name = name.Trim();
        if (name.Length == 0) return "請輸入帳號名稱";
        if (name.Length > 40) return "名稱不可超過 40 字";
        if (name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "名稱含有無效字元";
        if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return "名稱不可使用暫存目錄的 .tmp 結尾";
        if (existing.Contains(name, StringComparer.OrdinalIgnoreCase)) return "帳號名稱已存在";
        return null;
    }

    public void Save(string name)
    {
        var account = Path.Combine(storeDirectory, name);
        var temporary = account + ".tmp";
        Delete(temporary);
        try
        {
            var desktop = Path.Combine(temporary, "desktop");
            Directory.CreateDirectory(desktop);
            var config = ReadObject(Path.Combine(desktopDirectory, "config.json"));
            var auth = new JsonObject();
            foreach (var key in AuthKeys)
                if (config?.ContainsKey(key) == true) auth[key] = config[key]?.DeepClone();
            File.WriteAllText(Path.Combine(desktop, "auth.json"), auth.ToJsonString(), Utf8);
            foreach (var path in LoginPaths) Copy(Path.Combine(desktopDirectory, path), Path.Combine(desktop, path));
            Copy(credentialsPath, Path.Combine(temporary, "credentials.json"));
            var json = HasCredentials && File.Exists(claudePath) ? File.ReadAllText(claudePath) : "{}";
            var oauth = Path.Combine(temporary, "oauthAccount.json");
            if (JsonObjectText.Find(json, "oauthAccount") is { } span) File.WriteAllText(oauth, json.Substring(span.Start, span.Length), Utf8);
            else File.Delete(oauth);
            Delete(account);
            Directory.Move(temporary, account);
            Active = name;
        }
        finally { Delete(temporary); }
    }

    public void Restore(string name)
    {
        var account = Path.Combine(storeDirectory, name);
        var desktop = Path.Combine(account, "desktop");
        UpdateAuth(ReadObject(Path.Combine(desktop, "auth.json")));
        foreach (var path in LoginPaths) Copy(Path.Combine(desktop, path), Path.Combine(desktopDirectory, path));
        Copy(Path.Combine(account, "credentials.json"), credentialsPath);
        var oauth = Path.Combine(account, "oauthAccount.json");
        var json = File.Exists(claudePath) ? File.ReadAllText(claudePath) : "{}";
        File.WriteAllText(claudePath, HasCredentials && File.Exists(oauth) ? JsonObjectText.Replace(json, "oauthAccount", File.ReadAllText(oauth))
            : JsonObjectText.Remove(json, "oauthAccount"), Utf8);
        Active = name;
    }

    public void Clear()
    {
        foreach (var path in LoginPaths) Delete(Path.Combine(desktopDirectory, path));
        UpdateAuth(null);
        File.Delete(credentialsPath);
        if (File.Exists(claudePath))
            File.WriteAllText(claudePath, JsonObjectText.Remove(File.ReadAllText(claudePath), "oauthAccount"), Utf8);
    }

    public async Task WaitForCookiesAsync(TimeSpan timeout)
    {
        var path = Path.Combine(desktopDirectory, "Network", "Cookies");
        var elapsed = Stopwatch.StartNew();
        while (File.Exists(path))
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                return;
            }
            catch (IOException)
            {
                if (elapsed.Elapsed >= timeout) throw new IOException("Claude 桌面版的檔案仍被占用，請稍後再試");
            }
            await Task.Delay(300);
        }
    }

    private void UpdateAuth(JsonObject? auth)
    {
        var path = Path.Combine(desktopDirectory, "config.json");
        var config = ReadObject(path) ?? new JsonObject();
        foreach (var key in AuthKeys)
        {
            config.Remove(key);
            if (auth?.ContainsKey(key) == true) config[key] = auth[key]?.DeepClone();
        }
        Directory.CreateDirectory(desktopDirectory);
        File.WriteAllText(path, config.ToJsonString(), Utf8);
    }

    private static JsonObject? ReadObject(string path) => File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : null;

    private static void Delete(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, true);
        else if (File.Exists(path)) File.Delete(path);
    }

    private static void Copy(string source, string target)
    {
        Delete(target);
        if (File.Exists(source))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
        }
        else if (Directory.Exists(source))
        {
            Directory.CreateDirectory(target);
            foreach (var path in Directory.GetFileSystemEntries(source)) Copy(path, Path.Combine(target, Path.GetFileName(path)));
        }
    }
}
