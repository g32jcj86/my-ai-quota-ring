using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using QuotaRing.Core;

namespace QuotaRing;

internal sealed class ClaudeAccountSwitcher(Func<Task> beginSwitch, Func<string?, CancellationToken, Task> refresh)
{
    public bool IsBusy { get; private set; }
    private CancellationTokenSource? _loginCancellation;
    private string? _waitingAccount;

    public async Task AddMenuItemsAsync(ContextMenu menu)
    {
        try
        {
            var store = await Task.Run(CreateStore);
            var accounts = await Task.Run(() => (Accounts: store.List(), store.Active, store.IsMismatched));
            // Checked again before switching; this only greys out the menu while tasks are running.
            var runningTasks = await Task.Run(() => FindRunningTasks(forClaude: true));
            var blockedTip = runningTasks.Count > 0 ? "請等以下任務完成後再切換：" + Environment.NewLine + string.Join(Environment.NewLine, runningTasks.Select(task => "・" + task)) : null;
            if (blockedTip is not null)
            {
                var blocked = new MenuItem { Header = $"{runningTasks.Count} 個任務執行中，暫時無法切換", IsEnabled = false, ToolTip = blockedTip };
                ToolTipService.SetShowOnDisabled(blocked, true);
                menu.Items.Add(blocked);
            }
            foreach (var account in accounts.Accounts)
            {
                var item = new MenuItem
                {
                    Header = $"{account.Name}  {(account.Name == _waitingAccount ? "（等待登入）" : account.HasCredentials ? account.Email : "（CLI 未登入）")}" + (account.IsActive && accounts.IsMismatched ? "（桌面版與 CLI 帳號不一致）" : ""),
                    IsChecked = account.IsActive,
                    IsEnabled = !IsBusy && (account.IsActive || blockedTip is null),
                    ToolTip = account.IsActive ? null : blockedTip
                };
                ToolTipService.SetShowOnDisabled(item, true);
                item.Click += async (_, _) => { if (!account.IsActive) await RunAsync(store, "切換", account.Name); };
                menu.Items.Add(item);
            }
            if (accounts.Active is null)
            {
                var name = new MenuItem { Header = "命名目前帳號…", IsEnabled = !IsBusy };
                name.Click += async (_, _) => await RunAsync(store, "保存", null);
                menu.Items.Add(name);
            }
            var add = new MenuItem
            {
                Header = "新增帳號…",
                IsEnabled = accounts.Active is not null && !IsBusy && blockedTip is null,
                ToolTip = accounts.Active is null ? "請先命名目前帳號" : blockedTip
            };
            ToolTipService.SetShowOnDisabled(add, true);
            add.Click += async (_, _) => await RunAsync(store, "新增", null);
            menu.Items.Add(add);
            menu.Items.Add(new Separator());
        }
        catch (Exception exception)
        {
            await ReportFailureAsync("讀取帳號", "", exception);
        }
    }

    private static ClaudeAccountStore CreateStore()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Claude-Data");
        var directories = Directory.Exists(root) ? Directory.GetDirectories(root, "claude_*") : [];
        if (directories.Length != 1) throw new InvalidOperationException("找不到唯一的 Claude 桌面版資料目錄");
        return new ClaudeAccountStore(Path.Combine(home, ".claude-accounts"), directories[0],
            Path.Combine(home, ".claude", ".credentials.json"), Path.Combine(home, ".claude.json"));
    }

    private async Task RunAsync(ClaudeAccountStore store, string action, string? name)
    {
        if (IsBusy) return;
        IsBusy = true;
        var guideLogin = false;
        try
        {
            if (name is null)
            {
                var existing = await Task.Run(() => store.List().Select(account => account.Name).ToArray());
                name = AskName(existing, action, await Task.Run(() => store.CurrentEmail));
                if (name is null) return;
            }
            // Naming only records the active account; its login snapshot is saved when switching away.
            if (action == "保存")
            {
                await Task.Run(() => store.Active = name);
                await refresh(name, CancellationToken.None);
                return;
            }
            var runningTasks = await Task.Run(() => FindRunningTasks(forClaude: true));
            if (runningTasks.Count > 0)
                throw new InvalidOperationException("以下任務仍在執行，請等它們完成後再切換：" + Environment.NewLine + string.Join(Environment.NewLine, runningTasks.Select(task => "・" + task)));
            if (MessageBox.Show("將關閉 Claude 桌面版（包含進行中的 Code 工作階段），完成後自動重新開啟。", action + " Claude 帳號",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            CancelLogin();
            await beginSwitch();
            try
            {
                await Task.Run(async () =>
                {
                    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    var cliPath = Path.Combine(home, ".local", "bin", "claude.exe");
                    var desktopProcesses = new List<Process>();
                    var cliCount = 0;
                    foreach (var process in Process.GetProcessesByName("claude"))
                    {
                        var keep = false;
                        try
                        {
                            var path = process.MainModule?.FileName;
                            if (string.Equals(path, cliPath, StringComparison.OrdinalIgnoreCase)) cliCount++;
                            else if (path is not null && (path.Contains(@"\WindowsApps\Claude_", StringComparison.OrdinalIgnoreCase)
                                || path.StartsWith(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Claude-Data") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                            {
                                desktopProcesses.Add(process);
                                keep = true;
                            }
                        }
                        finally { if (!keep) process.Dispose(); }
                    }
                    try
                    {
                        if (cliCount > 0) throw new InvalidOperationException($"請先結束執行中的 Claude Code（{cliCount} 個）");
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                        foreach (var process in desktopProcesses)
                        {
                            if (process.HasExited) continue;
                            // Not the whole tree: anything started from a Code session (even this monitor) descends from Claude.exe.
                            process.Kill();
                            await process.WaitForExitAsync(timeout.Token);
                        }
                    }
                    finally { foreach (var process in desktopProcesses) process.Dispose(); }

                    await store.WaitForCookiesAsync(TimeSpan.FromSeconds(15));
                    if (store.Active is { } active && !string.Equals(active, name, StringComparison.OrdinalIgnoreCase)) store.Save(active);
                    if (action == "新增")
                    {
                        store.Clear();
                        store.Active = name;
                    }
                    else store.Restore(name);
                    var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Claude-Data");
                    var package = "Claude_" + Path.GetFileName(Directory.GetDirectories(root, "claude_*").Single())["claude_".Length..];
                    Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{package}!Claude") { UseShellExecute = true });
                    await LogAsync(action, name, "成功");
                });
                // Also guide accounts whose CLI was never logged in, e.g. added before this flow existed.
                guideLogin = action == "新增" || await Task.Run(() => store.List().Any(account => account.Name == name && !account.HasCredentials));
            }
            finally
            {
                await refresh(await Task.Run(() => store.Active), CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            await ReportFailureAsync(action, name ?? "", exception);
        }
        finally
        {
            IsBusy = false;
            if (guideLogin) _ = GuideLoginAsync(store, name!);
        }
    }

    public void CancelLogin()
    {
        _loginCancellation?.Cancel();
        _waitingAccount = null;
    }

    private async Task GuideLoginAsync(ClaudeAccountStore store, string name)
    {
        using var cancellation = new CancellationTokenSource();
        _loginCancellation = cancellation;
        _waitingAccount = name;
        var token = cancellation.Token;
        try
        {
            string? desktopUuid = null;
            var deadline = DateTimeOffset.UtcNow.AddMinutes(15);
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(2000, token);
                var desktop = await Task.Run(() => (store.Active, store.DesktopAccountUuid), token);
                token.ThrowIfCancellationRequested();
                if (desktop.Active != name) return;
                if (string.IsNullOrEmpty(desktop.DesktopAccountUuid)) continue;
                desktopUuid = desktop.DesktopAccountUuid;
                break;
            }
            if (desktopUuid is null) return;
            MessageBox.Show("桌面版已登入。接著會開啟終端機登入 Claude Code CLI，瀏覽器授權頁請確認是同一個帳號（若瀏覽器登入的是其他帳號，請先在 claude.ai 切換）。", "登入 Claude Code CLI", MessageBoxButton.OK, MessageBoxImage.Information);
            token.ThrowIfCancellationRequested();
            await Task.Run(() => Process.Start(new ProcessStartInfo("cmd.exe", $"/k \"\"{ClaudeUsageSource.FindClaudeCli()}\" auth login\"") { UseShellExecute = true }), token);

            deadline = DateTimeOffset.UtcNow.AddMinutes(15);
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(2000, token);
                var cli = await Task.Run(() => (store.Active, store.DesktopAccountUuid, store.CliAccountUuid, store.CurrentEmail), token);
                token.ThrowIfCancellationRequested();
                if (cli.Active != name) return;
                if (string.IsNullOrEmpty(cli.CliAccountUuid)) continue;
                _waitingAccount = null;
                if (cli.CliAccountUuid == cli.DesktopAccountUuid)
                {
                    MessageBox.Show($"『{name}』帳號已登入完成（{cli.CurrentEmail}）", "Claude 帳號登入完成");
                    token.ThrowIfCancellationRequested();
                    await refresh(name, token);
                }
                else
                    MessageBox.Show($"CLI 登入的帳號（{cli.CurrentEmail}）與桌面版不同，請在終端機執行 claude auth logout 後重新 claude auth login", "Claude 帳號不一致", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            await ReportFailureAsync("登入", name, exception);
        }
        finally
        {
            if (ReferenceEquals(_loginCancellation, cancellation))
            {
                _loginCancellation = null;
                _waitingAccount = null;
            }
        }
    }

    // Claude switching kills Claude sessions, so it is blocked by busy sessions and by Codex tasks they delegated;
    // Codex switching replaces auth.json, so it is blocked by every Codex exec task regardless of who started it.
    internal static List<string> FindRunningTasks(bool forClaude)
    {
        var tasks = new List<string>();
        // A full WMI process query takes seconds and delays the menu; the Toolhelp snapshot gives the parent map in milliseconds,
        // and WMI is only asked for codex.exe command lines.
        var processes = ProcessSnapshot();
        dynamic locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator")!)!;
        dynamic services = locator.ConnectServer(".", @"root\cimv2");
        dynamic rows = services.ExecQuery("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'codex.exe'");
        var codexCommands = new List<(int Id, string CommandLine)>();
        foreach (dynamic row in rows)
        {
            // SWbemObject dispatch IDs differ per instance, so dynamic's cached binding fails after the first item; Properties_ is stable.
            string? commandLine = row.Properties_.Item("CommandLine").Value;
            codexCommands.Add((Convert.ToInt32(row.Properties_.Item("ProcessId").Value),
                commandLine ?? throw new InvalidOperationException("無法讀取 Codex 命令列，無法確認執行中任務。")));
        }

        var sessions = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "sessions");
        if (forClaude && Directory.Exists(sessions))
        {
            var processIds = processes.Keys.ToHashSet();
            foreach (var path in Directory.GetFiles(sessions, "*.json"))
            {
                // Skip stale session files before parsing their contents.
                if (!int.TryParse(Path.GetFileNameWithoutExtension(path), out var pid) || !processIds.Contains(pid)) continue;
                var name = RunningTaskParser.ClaudeSession(File.ReadAllText(path), processIds);
                if (name is not null) tasks.Add("Claude Code：" + name);
            }
        }

        foreach (var (id, commandLine) in codexCommands)
        {
            var name = RunningTaskParser.CodexCommandLine(commandLine);
            if (name is null) continue;
            if (!forClaude) tasks.Add("Codex：" + name);
            else if (CodexProcessTree.HasAncestor(processes, id, "claude.exe")) tasks.Add("Codex（Claude 委派）：" + name);
        }
        if (!forClaude && processes.Values.Any(process => process.Name.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase)))
        {
            var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrWhiteSpace(codexHome)) codexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            var now = DateTime.Now;
            // Rollouts stay in the folder of the day the thread was created, so an old thread resumed today can be anywhere.
            var directory = Path.Combine(codexHome, "sessions");
            if (Directory.Exists(directory))
            {
                foreach (var file in new DirectoryInfo(directory).EnumerateFiles("rollout-*.jsonl", SearchOption.AllDirectories))
                {
                    if (file.LastWriteTime < now.AddHours(-2)) continue;
                    using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                    var firstLine = reader.ReadLine();
                    if (firstLine is null) continue;
                    reader.DiscardBufferedData();
                    var length = (int)Math.Min(stream.Length, 256 * 1024);
                    stream.Seek(-length, SeekOrigin.End);
                    var buffer = new byte[length];
                    var count = stream.ReadAtLeast(buffer, length, throwOnEndOfStream: false);
                    var name = RunningTaskParser.CodexRollout(firstLine, System.Text.Encoding.UTF8.GetString(buffer, 0, count));
                    if (name is not null) tasks.Add(name);
                }
            }
        }
        return tasks;
    }

    internal static Dictionary<int, (int ParentId, string Name)> ProcessSnapshot()
    {
        var processes = new Dictionary<int, (int ParentId, string Name)>();
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapprocess, 0);
        if (snapshot == -1) throw new Win32Exception();
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            for (var more = Process32First(snapshot, ref entry); more; more = Process32Next(snapshot, ref entry))
                processes[(int)entry.ProcessId] = ((int)entry.ParentProcessId, entry.ExeFile);
        }
        finally { _ = CloseHandle(snapshot); }
        return processes;
    }

    private const uint Th32csSnapprocess = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode)]
    private static extern bool Process32First(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode)]
    private static extern bool Process32Next(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    private static string? AskName(IEnumerable<string> existing, string action, string? email)
    {
        var dialog = new AccountNameDialog(existing, action, email);
        return dialog.ShowDialog() == true ? dialog.AccountName : null;
    }

    private static async Task ReportFailureAsync(string action, string name, Exception exception)
    {
        // Exception messages may contain JSON fragments; only log the exception type.
        try { await LogAsync(action, name, "失敗 " + exception.GetType().Name); }
        catch (Exception logException) when (logException is IOException or UnauthorizedAccessException) { }
        var reason = exception is InvalidOperationException or Win32Exception or OperationCanceledException or IOException or UnauthorizedAccessException or COMException
            ? exception.Message : "無法處理帳號檔案，請確認檔案格式與存取權限。";
        MessageBox.Show(reason, "Claude 帳號操作失敗", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static Task LogAsync(string action, string name, string result) => Task.Run(() =>
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuotaRing");
        Directory.CreateDirectory(directory);
        File.AppendAllText(Path.Combine(directory, "account-switch.log"), $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {action} {name} {result}{Environment.NewLine}");
    });
}
