using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using QuotaRing.Core;

namespace QuotaRing;

internal sealed class CodexAccountSwitcher(Func<Task> beginSwitch, Func<string?, CancellationToken, Task> refresh)
{
    public bool IsBusy { get; private set; }
    private CancellationTokenSource? _loginCancellation;
    private string? _waitingAccount;

    public async Task AddMenuItemsAsync(ContextMenu menu)
    {
        try
        {
            var store = await Task.Run(CreateStore);
            var accounts = await Task.Run(() => (Accounts: store.List(), store.Active));
            // Checked again before switching; this only greys out the menu while tasks are running.
            var runningTasks = await Task.Run(() => ClaudeAccountSwitcher.FindRunningTasks(forClaude: false));
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
                    Header = $"{account.Name}  {(account.Name == _waitingAccount ? "（等待登入）" : account.HasCredentials ? account.Email + (account.Plan is null ? "" : $"（{account.Plan}）") : "（未登入）")}",
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

    internal static CodexAccountStore CreateStore()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        return new CodexAccountStore(Path.Combine(home, ".codex-accounts"),
            Path.Combine(string.IsNullOrWhiteSpace(codexHome) ? Path.Combine(home, ".codex") : codexHome, "auth.json"));
    }

    private async Task RunAsync(CodexAccountStore store, string action, string? name)
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
            var runningTasks = await Task.Run(() => ClaudeAccountSwitcher.FindRunningTasks(forClaude: false));
            if (runningTasks.Count > 0)
                throw new InvalidOperationException("以下任務仍在執行，請等它們完成後再切換：" + Environment.NewLine + string.Join(Environment.NewLine, runningTasks.Select(task => "・" + task)));
            if (MessageBox.Show("將關閉 Codex 桌面版（包含進行中的 Codex 工作階段），完成後自動重新開啟。", action + " Codex 帳號",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            CancelLogin();
            try
            {
                await beginSwitch();
                await Task.Run(async () =>
                {
                    await StopDesktopAsync();
                    await store.WaitForAuthAsync(TimeSpan.FromSeconds(15));
                    if (store.Active is { } active && !string.Equals(active, name, StringComparison.OrdinalIgnoreCase)) store.Save(active);
                    if (action == "新增")
                    {
                        store.Clear();
                        store.Active = name;
                    }
                    else store.Restore(name);
                    Process.Start(new ProcessStartInfo("explorer.exe", @"shell:AppsFolder\OpenAI.Codex_2p2nqsd0c76g0!App") { UseShellExecute = true });
                    await LogAsync(action, name, "成功");
                });
                // Restoring an empty snapshot also requires login.
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

    private async Task GuideLoginAsync(CodexAccountStore store, string name)
    {
        using var cancellation = new CancellationTokenSource();
        _loginCancellation = cancellation;
        _waitingAccount = name;
        var token = cancellation.Token;
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddMinutes(15);
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(2000, token);
                var account = await Task.Run(() => (store.Active, store.HasCredentials, store.CurrentEmail), token);
                token.ThrowIfCancellationRequested();
                if (account.Active != name) return;
                if (!account.HasCredentials) continue;
                _waitingAccount = null;
                MessageBox.Show($"『{name}』帳號已登入完成（{account.CurrentEmail}）", "Codex 帳號登入完成");
                token.ThrowIfCancellationRequested();
                await refresh(name, token);
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

    // ChatGPT.exe is also the name of the standalone ChatGPT app; only the Codex package counts.
    private static bool IsCodexDesktop(int id)
    {
        using var process = Process.GetProcessById(id);
        return process.MainModule?.FileName.Contains(@"\WindowsApps\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static async Task StopDesktopAsync()
    {
        var processes = ClaudeAccountSwitcher.ProcessSnapshot();
        var roots = processes.Where(process => process.Value.Name.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase)
            && !(processes.TryGetValue(process.Value.ParentId, out var parent) && parent.Name.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase))
            && IsCodexDesktop(process.Key)).Select(process => process.Key).ToArray();
        var ids = CodexProcessTree.DesktopProcesses(processes.Select(process => (process.Key, process.Value.ParentId)).ToArray(), roots, Environment.ProcessId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        foreach (var id in ids)
        {
            Process process;
            try { process = Process.GetProcessById(id); }
            catch (ArgumentException) { continue; } // Already exited with its parent.
            using (process)
            {
                if (process.HasExited) continue;
                process.Kill();
                await process.WaitForExitAsync(timeout.Token);
            }
        }
    }

    private static string? AskName(IEnumerable<string> existing, string action, string? email)
    {
        var dialog = new AccountNameDialog(existing, action, email, "Codex");
        return dialog.ShowDialog() == true ? dialog.AccountName : null;
    }

    private static async Task ReportFailureAsync(string action, string name, Exception exception)
    {
        // Exception messages may contain JSON fragments; only log the exception type.
        try { await LogAsync(action, name, "失敗 " + exception.GetType().Name); }
        catch (Exception logException) when (logException is IOException or UnauthorizedAccessException) { }
        var reason = exception is InvalidOperationException or Win32Exception or OperationCanceledException or IOException or UnauthorizedAccessException or COMException
            ? exception.Message : "無法處理帳號檔案，請確認檔案格式與存取權限。";
        MessageBox.Show(reason, "Codex 帳號操作失敗", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static Task LogAsync(string action, string name, string result) => Task.Run(() =>
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuotaRing");
        Directory.CreateDirectory(directory);
        File.AppendAllText(Path.Combine(directory, "account-switch.log"), $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} Codex {action} {name} {result}{Environment.NewLine}");
    });
}
