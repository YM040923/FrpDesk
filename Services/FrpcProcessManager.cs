using System.IO;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using FrpDesk.Models;
using System.Windows.Threading;

namespace FrpDesk.Services;

public sealed class FrpcProcessManager
{
    private readonly string _frpcPath;
    private readonly Dispatcher _dispatcher;
    private readonly ProfileStore _store;
    private readonly ConcurrentDictionary<Guid, Process> _processes = new();
    private readonly ConcurrentDictionary<Guid, byte> _stopping = new();

    public FrpcProcessManager(string frpcPath, Dispatcher dispatcher, ProfileStore store)
    {
        _frpcPath = frpcPath;
        _dispatcher = dispatcher;
        _store = store;
    }

    public async Task StartAsync(TunnelProfile profile, string configPath)
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
                process.Kill(entireProcessTree: true);
                process.Dispose();
                throw new InvalidOperationException("该实例已有进程在运行。");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            profile.ClearDiagnostic();
            SaveDiagnosticSafely(profile);
            profile.State = "进程运行中";
            AddLog(profile, "配置校验通过，frpc 已启动。");
            if (process.HasExited) HandleExit(profile, process);
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

    public async Task StopAsync(TunnelProfile profile)
    {
        if (!_processes.TryGetValue(profile.Id, out var process))
        {
            profile.State = "已停止";
            return;
        }

        profile.IsBusy = true;
        _stopping[profile.Id] = 0;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            HandleExit(profile, process);
        }
        finally
        {
            profile.IsBusy = false;
            _stopping.TryRemove(profile.Id, out _);
        }
    }

    public async Task StopAllAsync(IEnumerable<TunnelProfile> profiles)
    {
        var running = profiles.Where(profile => _processes.ContainsKey(profile.Id)).ToArray();
        await Task.WhenAll(running.Select(StopAsync));
    }

    public async Task ReloadAsync(TunnelProfile profile, string configPath)
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
            profile.State = "重载失败";
            if (!profile.HasDiagnostic) ApplyDiagnostic(profile, Diagnostics.ReloadFailed(exception));
            AddLog(profile, exception.Message);
            throw;
        }
        finally
        {
            profile.IsBusy = false;
        }
    }

    public async Task<IReadOnlyList<string>> StatusAsync(TunnelProfile profile, string configPath)
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
            process.Kill(entireProcessTree: true);
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
        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        var diagnostic = Diagnostics.RuntimeLine(message);
        _dispatcher.BeginInvoke(() =>
        {
            profile.AddLog(line);
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
            _dispatcher.BeginInvoke(() =>
            {
                profile.State = expected ? "已停止" : "已退出";
                if (!expected)
                {
                    var diagnostic = Diagnostics.ProcessExited(exitCode);
                    profile.SetDiagnostic(diagnostic.Summary, diagnostic.Detail, diagnostic.Severity, diagnostic.LastExitCode, diagnostic.LastFailureAt);
                    SaveDiagnosticSafely(profile);
                    profile.AddLog($"{DateTime.Now:HH:mm:ss}  frpc 已退出（代码 {exitCode}）。");
                }
            }, null);
            process.Dispose();
        }
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
