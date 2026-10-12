using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using FrpDesk.Models;

namespace FrpDesk.Services;

public enum ServiceRuntimeState
{
    Starting,
    Running,
    Retrying,
    Failed,
    ConfigurationFailed,
    Stopped
}

public sealed record ServiceRuntimeStatus(
    ServiceRuntimeState State,
    int RetryCount,
    int? LastExitCode,
    int RetryDelaySeconds,
    DateTimeOffset UpdatedAt,
    string? DiagnosticSummary = null,
    string? DiagnosticDetail = null,
    DiagnosticSeverity DiagnosticSeverity = DiagnosticSeverity.None)
{
    public string ToDisplayText() => State switch
    {
        ServiceRuntimeState.Starting => "后台启动中",
        ServiceRuntimeState.Running => "后台运行中",
        ServiceRuntimeState.Retrying => $"后台重试中（第 {RetryCount} 次，{Math.Max(0, RetryDelaySeconds - (int)(DateTimeOffset.Now - UpdatedAt).TotalSeconds)} 秒后）",
        ServiceRuntimeState.Failed => $"后台重试耗尽（退出代码 {LastExitCode?.ToString() ?? "未知"}）",
        ServiceRuntimeState.ConfigurationFailed => "后台配置错误",
        _ => "后台已停止"
    };
}

public static class ServiceRuntime
{
    public static bool TryRun(string[] args)
    {
        if (!args.Any(argument => argument.Equals("--service-run", StringComparison.OrdinalIgnoreCase))) return false;

        var values = ParseArguments(args);
        var options = new ServiceOptions(
            GetRequired(values, "profile-id"),
            GetRequired(values, "frpc-path"),
            GetRequired(values, "config-path"),
            GetRequired(values, "log-path"),
            GetRequired(values, "status-path"));
        ServiceBase.Run(new FrpcWindowsService(options));
        return true;
    }

    private static Dictionary<string, string> ParseArguments(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal)) continue;
            if (args[index].Equals("--service-run", StringComparison.OrdinalIgnoreCase)) continue;
            if (index + 1 >= args.Length) continue;
            values[args[index][2..]] = args[++index];
        }
        return values;
    }

    private static string GetRequired(IReadOnlyDictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"服务参数缺少 --{name}。");
}

internal sealed record ServiceOptions(string ProfileId, string FrpcPath, string ConfigPath, string LogPath, string StatusPath);

internal sealed class FrpcWindowsService : ServiceBase
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(4)
    ];

    private readonly ServiceOptions _options;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _logLock = new();
    private ServiceRuntimeStatus? _lastStatus;
    private DiagnosticSnapshot? _diagnostic;
    private Task? _worker;
    private Process? _frpc;

    public FrpcWindowsService(ServiceOptions options)
    {
        _options = options;
        ServiceName = "FrpDesk-Instance-" + options.ProfileId;
        CanStop = true;
        CanShutdown = true;
        AutoLog = false;
    }

    protected override void OnStart(string[] args)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_options.LogPath)!);
        WriteStatus(new(ServiceRuntimeState.Starting, 0, null, 0, DateTimeOffset.Now));
        AppendLog("Windows 服务已启动。");
        _worker = RunAsync();
    }

    protected override void OnStop()
    {
        _stop.Cancel();
        KillFrpc();
        try { _worker?.GetAwaiter().GetResult(); } catch { }
        _diagnostic = null;
        WriteStatus(new(ServiceRuntimeState.Stopped, 0, null, 0, DateTimeOffset.Now));
        AppendLog("Windows 服务已停止。");
    }

    protected override void OnShutdown() => OnStop();

    private async Task RunAsync()
    {
        try
        {
            var verification = await VerifyConfigAsync();
            foreach (var line in verification.Output) AppendLog(line);
            if (verification.ExitCode != 0)
            {
                var diagnostic = Diagnostics.ConfigurationInvalid(verification.Output, verification.ExitCode);
                _diagnostic = diagnostic;
                WriteStatus(new(ServiceRuntimeState.ConfigurationFailed, 0, verification.ExitCode, 0, diagnostic.LastFailureAt!.Value,
                    diagnostic.Summary, diagnostic.Detail, diagnostic.Severity));
                AppendLog("配置校验失败，后台服务不会继续重试。");
                return;
            }
        }
        catch (Exception exception)
        {
            var diagnostic = Diagnostics.VerificationUnavailable(exception);
            _diagnostic = diagnostic;
            WriteStatus(new(ServiceRuntimeState.ConfigurationFailed, 0, null, 0, diagnostic.LastFailureAt!.Value,
                diagnostic.Summary, diagnostic.Detail, diagnostic.Severity));
            AppendLog($"配置校验无法完成：{exception.Message}");
            return;
        }

        for (var retryCount = 0; retryCount <= RetryDelays.Length; retryCount++)
        {
            if (_stop.IsCancellationRequested) return;
            Process? process = null;
            try
            {
                process = StartFrpc();
                _frpc = process;
                _diagnostic = null;
                WriteStatus(new(ServiceRuntimeState.Running, retryCount + 1, null, 0, DateTimeOffset.Now));
                AppendLog($"frpc 已启动（第 {retryCount + 1} 次）。");
                await process.WaitForExitAsync(_stop.Token);
                var exitCode = SafeExitCode(process);
                if (_stop.IsCancellationRequested) return;
                AppendLog($"frpc 已退出（代码 {exitCode}）。");
                if (retryCount == RetryDelays.Length)
                {
                    var diagnostic = Diagnostics.BackgroundFailed(exitCode, _diagnostic?.Detail);
                    _diagnostic = diagnostic;
                    WriteStatus(new(ServiceRuntimeState.Failed, retryCount + 1, exitCode, 0, diagnostic.LastFailureAt!.Value,
                        diagnostic.Summary, diagnostic.Detail, diagnostic.Severity));
                    AppendLog("已达到最大自动重试次数，请关闭后台服务后重新开启。");
                    return;
                }

                var delay = RetryDelays[retryCount];
                var retryDiagnostic = Diagnostics.BackgroundRetrying(retryCount + 1, exitCode, _diagnostic?.Detail);
                _diagnostic = retryDiagnostic;
                WriteStatus(new(ServiceRuntimeState.Retrying, retryCount + 1, exitCode, (int)delay.TotalSeconds, retryDiagnostic.LastFailureAt!.Value,
                    retryDiagnostic.Summary, retryDiagnostic.Detail, retryDiagnostic.Severity));
                AppendLog($"将在 {delay.TotalSeconds:0} 秒后自动重试（第 {retryCount + 1} 次）。");
                await Task.Delay(delay, _stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                var exitCode = process is null ? -1 : SafeExitCode(process);
                AppendLog($"启动或监控 frpc 失败：{exception.Message}");
                if (retryCount == RetryDelays.Length)
                {
                    var diagnostic = Diagnostics.BackgroundFailed(exitCode, $"启动或监控 frpc 失败：{exception.Message} 已达到最大自动重试次数，请检查运行日志后重新开启后台服务。");
                    _diagnostic = diagnostic;
                    WriteStatus(new(ServiceRuntimeState.Failed, retryCount + 1, exitCode, 0, diagnostic.LastFailureAt!.Value,
                        diagnostic.Summary, diagnostic.Detail, diagnostic.Severity));
                    return;
                }
                var delay = RetryDelays[retryCount];
                var retryDiagnostic = Diagnostics.BackgroundRetrying(retryCount + 1, exitCode, $"启动或监控 frpc 失败：{exception.Message} 将在稍后自动重试。");
                _diagnostic = retryDiagnostic;
                WriteStatus(new(ServiceRuntimeState.Retrying, retryCount + 1, exitCode, (int)delay.TotalSeconds, retryDiagnostic.LastFailureAt!.Value,
                    retryDiagnostic.Summary, retryDiagnostic.Detail, retryDiagnostic.Severity));
                await Task.Delay(delay, _stop.Token);
            }
            finally
            {
                if (ReferenceEquals(_frpc, process)) _frpc = null;
                process?.Dispose();
            }
        }
    }

    private Process StartFrpc()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.FrpcPath,
            WorkingDirectory = Path.GetDirectoryName(_options.ConfigPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(_options.ConfigPath);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) AppendLog(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) AppendLog(e.Data); };
        if (!process.Start()) throw new InvalidOperationException("frpc 进程未能启动。");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private async Task<(int ExitCode, IReadOnlyList<string> Output)> VerifyConfigAsync()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.FrpcPath,
            WorkingDirectory = Path.GetDirectoryName(_options.ConfigPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("verify");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(_options.ConfigPath);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法运行 frpc 配置校验。");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        var output = ((await outputTask) + Environment.NewLine + (await errorTask))
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (output.Length == 0) output = [process.ExitCode == 0 ? "frpc verify：配置有效。" : "frpc verify：配置无效。"];
        return (process.ExitCode, output);
    }

    private void KillFrpc()
    {
        try
        {
            if (_frpc is { HasExited: false }) _frpc.Kill(entireProcessTree: true);
        }
        catch { }
    }

    private void AppendLog(string message)
    {
        var diagnostic = Diagnostics.RuntimeLine(message);
        if (diagnostic is not null)
        {
            _diagnostic = diagnostic;
            if (_lastStatus is { State: ServiceRuntimeState.Running } status)
                WriteStatus(status with
                {
                    UpdatedAt = diagnostic.LastFailureAt ?? DateTimeOffset.Now,
                    DiagnosticSummary = diagnostic.Summary,
                    DiagnosticDetail = diagnostic.Detail,
                    DiagnosticSeverity = diagnostic.Severity
                });
        }

        lock (_logLock)
        {
            var bytes = Encoding.UTF8.GetBytes($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
            using var stream = new FileStream(_options.LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            stream.Write(bytes);
        }
    }

    private void WriteStatus(ServiceRuntimeStatus status)
    {
        try
        {
            if (_diagnostic is not null && string.IsNullOrWhiteSpace(status.DiagnosticSummary))
            {
                status = status with
                {
                    DiagnosticSummary = _diagnostic.Summary,
                    DiagnosticDetail = _diagnostic.Detail,
                    DiagnosticSeverity = _diagnostic.Severity
                };
            }
            _lastStatus = status;
            Directory.CreateDirectory(Path.GetDirectoryName(_options.StatusPath)!);
            var tempPath = _options.StatusPath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(status), new UTF8Encoding(false));
            File.Move(tempPath, _options.StatusPath, true);
        }
        catch (IOException) { }
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; } catch { return -1; }
    }
}
