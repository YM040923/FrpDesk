using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.ServiceProcess;
using System.Collections.Concurrent;
using FrpDesk.Models;

namespace FrpDesk.Services;

/// <summary>
/// Installs one Windows service per FRP profile. The service host owns frpc so
/// it survives desktop logoff and can restart frpc after an unexpected exit.
/// </summary>
public sealed class BackgroundTaskManager
{
    private const string ServicePrefix = "FrpDesk-Instance-";
    private readonly string _frpcPath;
    private readonly ProfileStore _store;
    private readonly ConcurrentDictionary<Guid, long> _logOffsets = new();

    public BackgroundTaskManager(string frpcPath, ProfileStore store)
    {
        _frpcPath = frpcPath;
        _store = store;
    }

    public async Task StartAsync(TunnelProfile profile)
    {
        try
        {
            if (!File.Exists(_frpcPath))
                throw new FileNotFoundException("找不到内置 frpc.exe，无法启用后台服务。", _frpcPath);
            _store.SaveConfig(profile);
            var request = CreateAdminRequest(profile, "install");
            await RunAdminHelperAsync(request);
            profile.ClearDiagnostic();
            _store.SaveDiagnostic(profile);
            profile.IsBackgroundConfigured = true;
            profile.IsBackgroundRunning = true;
            profile.BackgroundStatus = "后台启动中";
            profile.AddLog($"{DateTime.Now:HH:mm:ss}  已切换为 Windows 服务运行。关闭窗口、注销或断开 RDP 不会停止此实例。");
        }
        catch (Exception exception)
        {
            var diagnostic = Diagnostics.BackgroundServiceFailed(exception);
            profile.SetDiagnostic(diagnostic.Summary, diagnostic.Detail, diagnostic.Severity, diagnostic.LastExitCode, diagnostic.LastFailureAt);
            try { _store.SaveDiagnostic(profile); } catch (IOException) { }
            throw;
        }
    }

    public async Task StopAsync(TunnelProfile profile)
        => await StopManyAsync([profile]);

    public async Task StopManyAsync(IEnumerable<TunnelProfile> profiles)
    {
        var candidates = profiles.Where(profile => profile.IsBackgroundConfigured).ToArray();
        if (candidates.Length == 0) return;
        try
        {
            await RunAdminHelperAsync(CreateAdminRequest("remove", candidates));
            foreach (var profile in candidates)
            {
                profile.IsBackgroundConfigured = false;
                profile.IsBackgroundRunning = false;
                profile.BackgroundStatus = "后台已停止";
                if (!profile.IsRunning) profile.State = "已停止";
                profile.AddLog($"{DateTime.Now:HH:mm:ss}  Windows 服务后台运行已停止。");
            }
        }
        catch (Exception exception)
        {
            foreach (var profile in candidates)
            {
                var diagnostic = Diagnostics.BackgroundServiceFailed(exception);
                profile.SetDiagnostic(diagnostic.Summary, diagnostic.Detail, diagnostic.Severity, diagnostic.LastExitCode, diagnostic.LastFailureAt);
                try { _store.SaveDiagnostic(profile); } catch (IOException) { }
            }
            throw;
        }
    }

    public async Task RestartAsync(TunnelProfile profile)
    {
        profile.IsBusy = true;
        try
        {
            _store.SaveConfig(profile);
            await RunAdminHelperAsync(CreateAdminRequest(profile, "restart"));
            profile.IsBackgroundConfigured = true;
            profile.IsBackgroundRunning = true;
            profile.BackgroundStatus = "后台启动中";
            profile.AddLog($"{DateTime.Now:HH:mm:ss}  Windows 后台服务已重启，新配置正在生效。");
        }
        catch (Exception exception)
        {
            var diagnostic = Diagnostics.BackgroundServiceFailed(exception);
            profile.SetDiagnostic(diagnostic.Summary, diagnostic.Detail, diagnostic.Severity, diagnostic.LastExitCode, diagnostic.LastFailureAt);
            try { _store.SaveDiagnostic(profile); } catch (IOException) { }
            throw;
        }
        finally
        {
            profile.IsBusy = false;
        }
    }

    public Task LoadStateAsync(IEnumerable<TunnelProfile> profiles) => RefreshStateAsync(profiles, true);

    public Task RefreshStateAsync(IEnumerable<TunnelProfile> profiles) => RefreshStateAsync(profiles, false);

    private async Task RefreshStateAsync(IEnumerable<TunnelProfile> profiles, bool loadLogs)
    {
        foreach (var profile in profiles)
        {
            var serviceName = ServiceName(profile);
            var queriedStatus = await QueryServiceStatusAsync(serviceName);
            var serviceConfigured = queriedStatus is not null;
            var legacyTaskConfigured = !serviceConfigured && await LegacyTaskExistsAsync(profile);
            var configured = serviceConfigured || legacyTaskConfigured;
            var status = serviceConfigured ? ReadStatus(profile) : null;
            var running = serviceConfigured
                ? IsServiceRunning(queriedStatus!.Value, status)
                : legacyTaskConfigured && await IsLegacyTaskRunningAsync(profile);
            var wasRunning = profile.IsBackgroundRunning;
            profile.IsBackgroundConfigured = configured;
            profile.IsBackgroundRunning = running;
            profile.BackgroundStatus = configured
                ? legacyTaskConfigured ? "旧版后台任务已配置，点击切换为服务" : status?.ToDisplayText() ?? (running ? "后台运行中" : "后台已停止")
                : "后台已停止";

            if (status is { DiagnosticSummary: not null, DiagnosticDetail: not null } diagnostic && diagnostic.DiagnosticSeverity != DiagnosticSeverity.None)
            {
                profile.SetDiagnostic(diagnostic.DiagnosticSummary!, diagnostic.DiagnosticDetail!, diagnostic.DiagnosticSeverity,
                    diagnostic.LastExitCode, diagnostic.UpdatedAt);
                try { _store.SaveDiagnostic(profile); } catch (IOException) { }
            }
            else if (status?.State == ServiceRuntimeState.Running && profile.HasDiagnostic)
            {
                profile.ClearDiagnostic();
                try { _store.SaveDiagnostic(profile); } catch (IOException) { }
            }

            if (configured && status?.State == ServiceRuntimeState.ConfigurationFailed && !profile.IsRunning)
                profile.State = "后台配置错误";
            else if (configured && status?.State == ServiceRuntimeState.Failed && !profile.IsRunning)
                profile.State = "后台重试耗尽";
            else if (configured && !running && !profile.IsRunning && status?.State is not (ServiceRuntimeState.Failed or ServiceRuntimeState.ConfigurationFailed))
                profile.State = "后台已停止";
            else if (!configured && !profile.IsRunning)
                profile.State = "已停止";

            if (configured && (loadLogs || running || wasRunning)) LoadLog(profile);
        }
    }

    public async Task<bool> ExistsAsync(TunnelProfile profile) =>
        await QueryServiceStatusAsync(ServiceName(profile)) is not null;

    private static Task<ServiceControllerStatus?> QueryServiceStatusAsync(string serviceName) => Task.Run<ServiceControllerStatus?>(() =>
    {
        try
        {
            using var controller = new ServiceController(serviceName);
            return controller.Status;
        }
        catch (InvalidOperationException) { return null; }
        catch (System.ComponentModel.Win32Exception) { return null; }
    });

    private static bool IsServiceRunning(ServiceControllerStatus serviceStatus, ServiceRuntimeStatus? status)
    {
        if (status is { State: ServiceRuntimeState.Failed or ServiceRuntimeState.ConfigurationFailed or ServiceRuntimeState.Stopped }) return false;
        return serviceStatus is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending or ServiceControllerStatus.ContinuePending;
    }

    private string ServiceName(TunnelProfile profile) => ServicePrefix + profile.Id.ToString("N");

    private WindowsServiceAdminRequest CreateAdminRequest(TunnelProfile profile, string action) =>
        CreateAdminRequest(action, [profile]);

    private static WindowsServiceAdminRequest CreateAdminRequest(string action, IEnumerable<TunnelProfile> profiles) =>
        new(action, profiles.Select(profile => profile.Id.ToString("N")).ToArray());

    private async Task RunAdminHelperAsync(WindowsServiceAdminRequest request)
    {
        var executable = ResolveServiceExecutable();
        var resultDirectory = Path.Combine(_store.RootDirectory, "admin-results");
        Directory.CreateDirectory(resultDirectory);
        var resultPath = Path.Combine(resultDirectory, "frpc-admin-" + Guid.NewGuid().ToString("N") + ".txt");
        var payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(request with { ResultPath = resultPath }));
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("--background-admin");
        startInfo.ArgumentList.Add(payload);
        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 Windows 服务管理程序。");
        }
        catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("已取消管理员权限，后台服务未更改。");
        }

        using (process) await process.WaitForExitAsync();
        try
        {
            if (process.ExitCode != 0)
            {
                var details = File.Exists(resultPath) ? File.ReadAllText(resultPath).Trim() : string.Empty;
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(details)
                    ? $"Windows 服务操作失败（代码 {process.ExitCode}）。"
                    : details);
            }
        }
        finally
        {
            if (File.Exists(resultPath)) File.Delete(resultPath);
        }
    }

    private string LogPath(TunnelProfile profile) => Path.Combine(_store.ProfileDirectory(profile), "frpc-background.log");
    private string StatusPath(TunnelProfile profile) => Path.Combine(_store.ProfileDirectory(profile), "frpc-background.status.json");

    private static string ResolveServiceExecutable()
    {
        var current = Environment.ProcessPath;
        if (current is not null && Path.GetFileName(current).Equals("FrpDesk.exe", StringComparison.OrdinalIgnoreCase)) return current;
        var published = Path.Combine(AppContext.BaseDirectory, "FrpDesk.exe");
        if (File.Exists(published)) return published;
        throw new InvalidOperationException("Windows 服务需要使用已构建的 FrpDesk.exe 启用，请先运行发布版本。");
    }

    private ServiceRuntimeStatus? ReadStatus(TunnelProfile profile)
    {
        try
        {
            var path = StatusPath(profile);
            return File.Exists(path) ? JsonSerializer.Deserialize<ServiceRuntimeStatus>(File.ReadAllText(path)) : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    private void LoadLog(TunnelProfile profile)
    {
        var path = LogPath(profile);
        if (!File.Exists(path)) return;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var offset = _logOffsets.GetValueOrDefault(profile.Id);
            if (stream.Length < offset) offset = 0;
            stream.Position = offset;
            using var reader = new StreamReader(stream, Encoding.UTF8, true, 1024, leaveOpen: true);
            while (reader.ReadLine() is { } line) profile.AddLog(line);
            _logOffsets[profile.Id] = stream.Position;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static async Task<bool> LegacyTaskExistsAsync(TunnelProfile profile)
    {
        var result = await RunSchtasksAsync(true, false, "/Query", "/TN", ServicePrefix + profile.Id.ToString("N"));
        return result.ExitCode == 0;
    }

    private static async Task<bool> IsLegacyTaskRunningAsync(TunnelProfile profile)
    {
        var result = await RunSchtasksAsync(true, false, "/Query", "/TN", ServicePrefix + profile.Id.ToString("N"), "/FO", "LIST");
        return result.Output.Contains("Running", StringComparison.OrdinalIgnoreCase)
            || result.Output.Contains("正在运行", StringComparison.OrdinalIgnoreCase);
    }

    private static Task<(int ExitCode, string Output)> RunSchtasksAsync(bool allowFailure, bool elevate, params string[] arguments) =>
        RunToolAsync("schtasks.exe", allowFailure, elevate, arguments);

    private static async Task<(int ExitCode, string Output)> RunToolAsync(string fileName, bool allowFailure, bool elevate, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = elevate,
            CreateNoWindow = !elevate,
            RedirectStandardOutput = !elevate,
            RedirectStandardError = !elevate,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            Verb = elevate ? "runas" : string.Empty,
            WindowStyle = elevate ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException($"无法启动 {fileName}。");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("已取消管理员权限，后台服务未启动。");
        }

        using (process)
        {
            if (elevate)
            {
                await process.WaitForExitAsync();
                if (process.ExitCode != 0 && !allowFailure)
                    throw new InvalidOperationException($"Windows 服务操作失败（代码 {process.ExitCode}）。");
                return (process.ExitCode, string.Empty);
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = (await outputTask) + (await errorTask);
            if (process.ExitCode != 0 && !allowFailure) throw new InvalidOperationException(output.Trim());
            return (process.ExitCode, output);
        }
    }
}
