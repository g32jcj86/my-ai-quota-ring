using System.Text.Json;
using System.IO;
using QuotaRing.Core;

namespace QuotaRing;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true
    };
    private readonly string _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuotaRing");
    private string PathName => Path.Combine(_directory, "settings.json");

    public AppSettings? Load()
    {
        try
        {
            if (!File.Exists(PathName)) return null;

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(PathName))?.Normalize();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(AppSettings settings)
    {
        settings = settings.Normalize();
        Directory.CreateDirectory(_directory);
        File.WriteAllText(PathName, JsonSerializer.Serialize(settings, Options));
    }

    public void Reset()
    {
        if (File.Exists(PathName))
        {
            File.Delete(PathName);
        }

        StartupManager.Apply(false);
    }
}

public static class StartupManager
{
    private const string ShortcutName = "QuotaRing.lnk";
    private static string ShortcutPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), ShortcutName);
    private static string ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "QuotaRing.exe");

    public static void Apply(bool enabled)
    {
        if (!enabled)
        {
            if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
            return;
        }

        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("無法建立開機啟動捷徑");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(ShortcutPath);
        shortcut.TargetPath = ExecutablePath;
        shortcut.WorkingDirectory = System.IO.Path.GetDirectoryName(ExecutablePath);
        shortcut.Description = "QuotaRing";
        shortcut.Save();
    }

    public static void RepairIfNeeded(bool enabled)
    {
        if (enabled) Apply(true);
        if (!enabled && File.Exists(ShortcutPath)) Apply(false);
    }
}
