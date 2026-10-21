using System.IO;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using FrpDesk.Models;

namespace FrpDesk.Services;

public sealed class FrpcProcessManager
{
    private readonly string _frpcPath;
    private readonly Action<Action> _dispatch;
    private readonly ProfileStore _store;
    private readonly ConcurrentDictionary<Guid, Process> _processes = new();
    private readonly ConcurrentDictionary<Guid, byte> _stopping = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public FrpcProcessManager(string frpcPath, Action<Action> dispatch, ProfileStore store)
    {
        _frpcPath = frpcPath;
        _dispatch = dispatch;
        _store = store;
    }

    public async Task StartAsync(TunnelProfile profile, string configPath)
    {
        var gate = GetLock(profile.Id);
        await gate.WaitAsync();
        try
        {
            if (_processes.ContainsKey(profile.Id)) return;
            profile.IsBusy = true;
            profile.State = "正在检查配置";
            try
            {
                (int ExitCode, IReadOnlyList<string> Output) verify;
                try
                {
                    verify = await RunVerificationAsync(configPath, Path.GetDirectoryName(configPath)!);
                }
                catch (Exception exception)
                {
                    ApplyDiagnostic(profile, Diagnostics.VerificationUnavailable(exception));
                    throw;
                }

                foreach (var line in verify.Output) AddLog(profile, line);
                if (verify.ExitCode != 0)
                {
                    var diagnostic = Diagnostics.ConfigurationInvalid(verify.Output, verify.ExitCode);
                    ApplyDiagnostic(profile, diagnostic);
                    throw new InvalidDataException(diagnostic.Detail);
                }

                var startInfo = CreateStartInfo(Path.GetDirectoryName(configPath)!);
                startInfo.ArgumentList.Add("-c");
                startInfo.ArgumentList.Add(configPath);
                var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                process.OutputDataReceived += (_, e) => { if (e.Data is not null) AddLog(profile, e.Data); };
                process.ErrorDataReceived += (_, e) => { if (e.Data is not null) AddLog(profile, e.Data); };
                process.Exited += (_, _) => HandleExit(profile, process);

                if (!process.Start()) throw new InvalidOperationException("frpc 进程未能启动。");
                if (!_processes.TryAdd(profile.Id, process))
                {
                    try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    process.Dispose();
                    throw new InvalidOperationException("该实例已有进程在运行。");
                }

                process.Exited += (_, _) => HandleExit(profile, process);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (process.HasExited)
                {
                    HandleExit(profile, process);
                    return;
                }
                profile.ClearDiagnostic();
                SaveDiagnosticSafely(profile);
                profile.State = "进程运行中";
                profile.MarkStarted();
                AddLog(profile, "配置校验通过，frpc 已启动。");
            }
            catch (Exception exception)
            {
                profile.State = "启动失败";
                if (!profile.HasDiagnostic)
                {
                    ApplyDiagnostic(profile, Diagnostics.ProcessStartFailed(exception));
                }
                AddLog(profile, exception.Message);
                throw;
            }
            finally
            {
                profile.IsBusy = false;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task StopAsync(TunnelProfile profile)
    {
        var gate = GetLock(profile.Id);
        await gate.WaitAsync();
        try
        {
            if (!_processes.TryGetValue(profile.Id, out var process))
            {
                profile.State = "已停止";
                if (profile.StartedAt is not null) profile.MarkStopped();
                return;
            }

            profile.IsBusy = true;
            _stopping[profile.Id] = 0;
            try
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
                {
                    // 进程恰好在停止前退出，Exited 回调已完成状态更新。
                }
                catch (System.ComponentModel.Win32Exception exception)
                {
                    throw new InvalidOperationException($"无法结束 frpc 进程：{exception.Message}", exception);
                }

                try
                {
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
                }
                catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
                {
                    // 同上：Process 对象已被退出回调释放。
                }
                catch (TimeoutException)
                {
                    throw new InvalidOperationException("frpc 未能在 8 秒内停止，请重试。");
                }

                HandleExit(profile, process);
            }
            finally
            {
                profile.IsBusy = false;
                _stopping.TryRemove(profile.Id, out _);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task StopAllAsync(IEnumerable<TunnelProfile> profiles)
    {
        await Task.WhenAll(profiles.Select(StopAsync));
    }

    public async Task ReloadAsync(TunnelProfile profile, string configPath)
    {
        var gate = GetLock(profile.Id);
        await gate.WaitAsync();
        try
        {
            if (!_processes.ContainsKey(profile.Id)) throw new InvalidOperationException("该实例当前没有正在运行的前台 frpc 进程。");
            profile.IsBusy = true;
            profile.State = "正在重载配置";
            try
            {
                var verify = await RunVerificationAsync(configPath, Path.GetDirectoryName(configPath)!);
                foreach (var line in verify.Output) AddLog(profile, line);
                if (verify.ExitCode != 0)
                {
                    var diagnostic = Diagnostics.ConfigurationInvalid(verify.Output, verify.ExitCode);
                    ApplyDiagnostic(profile, diagnostic);
                    throw new InvalidDataException(diagnostic.Detail);
                }

                var result = await RunCommandAsync("reload", configPath, Path.GetDirectoryName(configPath)!);
                foreach (var line in result.Output) AddLog(profile, line);
                if (result.ExitCode != 0)
                {
                    var detail = result.Output.LastOrDefault(line => !string.IsNullOrWhiteSpace(line)) ?? "frpc 管理接口未接受重载请求。";
                    throw new InvalidOperationException(detail);
                }

                profile.ClearDiagnostic();
                SaveDiagnosticSafely(profile);
                profile.State = "进程运行中";
                AddLog(profile, "配置已热更新。后台连接保持运行。");
            }
            catch (Exception exception)
            {
                // frpc is still alive; keep the instance stoppable instead of showing it as stopped.
                profile.State = "进程运行中";
                if (!profile.HasDiagnostic) ApplyDiagnostic(profile, Diagnostics.ReloadFailed(exception));
                AddLog(profile, exception.Message);
                throw;
            }
            finally
            {
                profile.IsBusy = false;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<string>> StatusAsync(TunnelProfile profile, string configPath)
    {
        var gate = GetLock(profile.Id);
        await gate.WaitAsync();
        try
        {
            if (!File.Exists(configPath)) throw new FileNotFoundException("找不到该实例的配置文件。", configPath);
            profile.IsBusy = true;
            try
            {
                var result = await RunCommandAsync("status", configPath, Path.GetDirectoryName(configPath)!);
                if (result.ExitCode != 0)
                {
                    var detail = result.Output.LastOrDefault(line => !string.IsNullOrWhiteSpace(line)) ?? "frpc 管理接口未返回状态。";
                    throw new InvalidOperationException(detail);
                }
                return result.Output;
            }
            finally
            {
                profile.IsBusy = false;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private SemaphoreSlim GetLock(Guid profileId) => _locks.GetOrAdd(profileId, _ => new SemaphoreSlim(1, 1));

    private async Task<(int ExitCode, IReadOnlyList<string> Output)> RunVerificationAsync(string configPath, string workingDirectory)
    {
        var startInfo = CreateStartInfo(workingDirectory);
        startInfo.ArgumentList.Add("verify");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(configPath);

        using var process = new Process { StartInfo = startInfo };
        var output = new ConcurrentQueue<string>();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.Enqueue(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.Enqueue(e.Data); };
        if (!process.Start()) throw new InvalidOperationException("无法运行 frpc 配置校验。");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (TimeoutException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
            throw new TimeoutException("配置校验超时，已终止校验进程。");
        }

        process.WaitForExit();
        if (output.IsEmpty) output.Enqueue(process.ExitCode == 0 ? "frpc verify：配置有效。" : "frpc verify：配置无效。");
        return (process.ExitCode, output.ToArray());
    }

    private async Task<(int ExitCode, IReadOnlyList<string> Output)> RunCommandAsync(string command, string configPath, string workingDirectory)
    {
        var startInfo = CreateStartInfo(workingDirectory);
        startInfo.ArgumentList.Add(command);
        startInfo.ArgumentList.Add("--api-timeout");
        startInfo.ArgumentList.Add("3s");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(configPath);
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException($"无法运行 frpc {command} 命令。");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException($"frpc {command} 命令超时。");
        }

        var output = ((await outputTask) + Environment.NewLine + (await errorTask))
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (output.Length == 0) output = [$"frpc {command} 已完成。"];
        return (process.ExitCode, output);
    }

    private ProcessStartInfo CreateStartInfo(string workingDirectory) => new()
    {
        FileName = _frpcPath,
        WorkingDirectory = workingDirectory,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8
    };

    private void AddLog(TunnelProfile profile, string message)
    {
        var cleaned = LogText.Clean(message);
        var diagnostic = Diagnostics.RuntimeLine(cleaned, ServerHint(profile.Config));
        _dispatch(() =>
        {
            profile.AddLog(cleaned);
            if (cleaned.Contains("login to server success", StringComparison.OrdinalIgnoreCase))
            {
                profile.ClearDiagnostic();
                SaveDiagnosticSafely(profile);
                return;
            }
            if (diagnostic is null) return;
            profile.SetDiagnostic(diagnostic.Summary, diagnostic.Detail, diagnostic.Severity, diagnostic.LastExitCode, diagnostic.LastFailureAt);
            SaveDiagnosticSafely(profile);
        });
    }

    private void HandleExit(TunnelProfile profile, Process process)
    {
        if (_processes.TryGetValue(profile.Id, out var current) && ReferenceEquals(current, process))
        {
            _processes.TryRemove(profile.Id, out _);
            var expected = _stopping.ContainsKey(profile.Id);
            var exitCode = SafeExitCode(process);
            _dispatch(() =>
            {
                profile.State = expected ? "已停止" : "已退出";
                profile.MarkStopped();
                if (!expected)
                {
                    var diagnostic = Diagnostics.ProcessExited(exitCode);
                    profile.SetDiagnostic(diagnostic.Summary, diagnostic.Detail, diagnostic.Severity, diagnostic.LastExitCode, diagnostic.LastFailureAt);
                    SaveDiagnosticSafely(profile);
                    profile.AddLog($"frpc 已退出（代码 {exitCode}）。");
                }
            });
            process.Dispose();
        }
    }

    private static string? ServerHint(string config)
    {
        var address = System.Text.RegularExpressions.Regex.Match(
            config, "(?m)^\\s*serverAddr\\s*=\\s*\"?([^\"\\r\\n]+)\"?").Groups[1].Value.Trim();
        if (address.Length == 0) return null;
        var port = System.Text.RegularExpressions.Regex.Match(
            config, "(?m)^\\s*serverPort\\s*=\\s*(\\d+)").Groups[1].Value;
        return port.Length == 0 ? address : $"{address}:{port}";
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch (InvalidOperationException) { return -1; }
    }

    private void ApplyDiagnostic(TunnelProfile profile, DiagnosticSnapshot diagnostic)
    {
        profile.SetDiagnostic(diagnostic.Summary, diagnostic.Detail, diagnostic.Severity, diagnostic.LastExitCode, diagnostic.LastFailureAt);
        SaveDiagnosticSafely(profile);
    }

    private void SaveDiagnosticSafely(TunnelProfile profile)
    {
        try
        {
            _store.SaveDiagnostic(profile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A diagnostic file is supplemental state and must never crash the UI.
        }
    }
}
