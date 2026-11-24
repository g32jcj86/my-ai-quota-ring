using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using QuotaRing.Core;

namespace QuotaRing;

public sealed class ClaudeUsageSource
{
    private static readonly HttpClient SharedClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(10)
    };
    private readonly HttpClient _client;
    private readonly string _credentialsPath;
    private readonly Func<Task<string>> _refreshCli;
    private readonly string _logDirectory;
    private DateTimeOffset _nextRead;
    private DateTimeOffset _nextRefresh;
    private QuotaSnapshot _snapshot = QuotaSnapshot.Offline("尚未取得 Claude Code 額度");

    public ClaudeUsageSource(HttpClient? client = null, string? credentialsPath = null, Func<Task<string>>? refreshCli = null, string? logDirectory = null)
    {
        _client = client ?? SharedClient;
        _refreshCli = refreshCli ?? RefreshWithClaudeCliAsync;
        _logDirectory = logDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuotaRing");
        var configDirectory = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        _credentialsPath = credentialsPath ?? Path.Combine(
            string.IsNullOrWhiteSpace(configDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
                : configDirectory, ".credentials.json");
    }

    public async Task<QuotaSnapshot> ReadAsync(DateTimeOffset now)
    {
        var credentialsExist = File.Exists(_credentialsPath);
        if (now < _nextRead && (_snapshot.Error != "找不到 Claude Code 登入資料" || !credentialsExist)) return _snapshot;
        _nextRead = now.AddSeconds(60);
        try
        {
            if (!credentialsExist)
                return _snapshot = QuotaSnapshot.Offline("找不到 Claude Code 登入資料");

            var credentials = ClaudeCredentials.Parse(await File.ReadAllTextAsync(_credentialsPath), now, out var error);
            if (credentials is null && error == ClaudeCredentials.ExpiredError && now >= _nextRefresh)
            {
                _nextRefresh = now.AddMinutes(10);
                var result = await _refreshCli();
                credentials = ClaudeCredentials.Parse(await File.ReadAllTextAsync(_credentialsPath), now, out error);
                Directory.CreateDirectory(_logDirectory);
                await File.AppendAllTextAsync(Path.Combine(_logDirectory, "claude-refresh.log"),
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {result} refreshed={credentials is not null}{Environment.NewLine}");
            }
            if (credentials is null) return _snapshot = QuotaSnapshot.Offline(error);

            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
            request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            using var response = await _client.SendAsync(request);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return _snapshot = QuotaSnapshot.Offline("Claude Code 登入已失效");

            if (response.IsSuccessStatusCode)
            {
                var parsed = ClaudeUsageParser.Parse(await response.Content.ReadAsStringAsync(), now);
                if (parsed.Error is null) return _snapshot = parsed;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or HttpRequestException or OperationCanceledException or FormatException)
        {
            // Keep all request/credential details out of user-visible errors.
        }

        return _snapshot = _snapshot.OrderedWindows.Count > 0
            ? _snapshot with { IsStale = true, Error = "無法更新 Claude Code 額度" }
            : QuotaSnapshot.Offline("無法更新 Claude Code 額度");
    }

    public QuotaSnapshot Reset()
    {
        _nextRead = default;
        _nextRefresh = default;
        return _snapshot = QuotaSnapshot.Offline("切換帳號中") with { IsStale = false };
    }

    internal static string FindClaudeCli()
    {
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
        return File.Exists(installed) ? installed : "claude";
    }

    // Let Claude Code refresh its own login: using the refresh token here would rotate it away from Claude Code.
    // "auth status" never refreshes; "-p /cost" runs locally without a model call but needs a valid token, so it refreshes first.
    // Returns a one-line summary for claude-refresh.log; stdout is drained but not logged.
    private static async Task<string> RefreshWithClaudeCliAsync()
    {
        var fileName = FindClaudeCli();
        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo(fileName, "-p /cost")
            {
                WorkingDirectory = Path.GetTempPath(),
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
        }
        catch (Win32Exception exception)
        {
            return $"start-failed file={fileName} error={exception.Message}";
        }
        if (process is null) return $"start-failed file={fileName}";

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(true);
                return $"timeout file={fileName}";
            }
            await stdout;
            var error = (await stderr).ReplaceLineEndings(" ").Trim();
            return $"exit={process.ExitCode} file={fileName} stderr={(error.Length > 300 ? error[..300] : error)}";
        }
    }
}
