using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using TimeoutException = System.TimeoutException;

namespace FrpDesk.Services;

public sealed record WindowsServiceAdminRequest(
    string Action,
    string[] ProfileIds,
    string? ResultPath = null);

public static class WindowsServiceAdmin
{
    private const string ServicePrefix = "FrpDesk-Instance-";

    public static bool TryRun(string[] args)
    {
        if (args.Length < 2 || !args[0].Equals("--background-admin", StringComparison.OrdinalIgnoreCase)) return false;
        WindowsServiceAdminRequest? request = null;
        try
        {
            request = JsonSerializer.Deserialize<WindowsServiceAdminRequest>(Convert.FromBase64String(args[1]))
                ?? throw new InvalidDataException("后台服务参数无效。");
            var profileIds = ValidateRequest(request);
            switch (request.Action)
            {
                case "install":
                    foreach (var profileId in profileIds) InstallAndStart(profileId);
                    break;
                case "remove":
                    foreach (var profileId in profileIds) StopAndRemove(profileId);
                    break;
                case "restart":
                    foreach (var profileId in profileIds) Restart(profileId);
                    break;
                default:
                    throw new InvalidDataException("不支持的服务操作。");
            }
            return true;
        }
        catch (Exception exception)
        {
            if (request is not null && IsAllowedResultPath(request.ResultPath))
            {
                try { File.WriteAllText(request.ResultPath!, exception.Message, new UTF8Encoding(false)); } catch { }
            }
            Environment.ExitCode = 1;
            return true;
        }
    }

    private static string[] ValidateRequest(WindowsServiceAdminRequest request)
    {
        if (request.ProfileIds is null || request.ProfileIds.Length is 0 or > 64)
            throw new InvalidDataException("实例标识数量无效。");
        var ids = request.ProfileIds.Select(value =>
        {
            if (!Guid.TryParseExact(value, "N", out var id))
                throw new InvalidDataException("实例标识无效。");
            return id.ToString("N");
        }).Distinct(StringComparer.Ordinal).ToArray();

        if (request.Action is not ("install" or "remove" or "restart"))
            throw new InvalidDataException("不支持的服务操作。");
        if (!IsAllowedResultPath(request.ResultPath))
            throw new InvalidDataException("服务操作结果路径无效。");
        return ids;
    }

    private static bool IsAllowedResultPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var resultRoot = Path.Combine(LocalRoot(), "admin-results");
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(Path.TrimEndingDirectorySeparator(resultRoot) + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(fullPath).StartsWith("frpc-admin-", StringComparison.Ordinal)
            && Path.GetExtension(fullPath).Equals(".txt", StringComparison.OrdinalIgnoreCase);
    }

    private static void InstallAndStart(string profileId)
    {
        RemoveLegacyTask(profileId);
        var serviceName = ServiceName(profileId);
        var runtimeDirectory = RuntimeDirectory(profileId);
        var instanceDirectory = InstanceDirectory(profileId);
        var serviceAccount = "NT SERVICE\\" + serviceName;
        var sourceExecutable = Environment.ProcessPath
            ?? throw new InvalidDataException("无法确认服务宿主程序路径。");
        var sourceFrpc = Path.Combine(Path.GetDirectoryName(sourceExecutable)!, "assets", "frpc.exe");
        if (!File.Exists(sourceFrpc))
            throw new FileNotFoundException("找不到内置 frpc.exe。", sourceFrpc);
        if (!Directory.Exists(instanceDirectory))
            throw new DirectoryNotFoundException("找不到实例数据目录。");

        var hostRoot = Path.GetDirectoryName(runtimeDirectory)!;
        Directory.CreateDirectory(hostRoot);
        SecureHostRoot(hostRoot);
        if (IsInstalled(serviceName)) StopAndDeleteExisting(serviceName);
        Directory.CreateDirectory(runtimeDirectory);
        File.Copy(sourceExecutable, Path.Combine(runtimeDirectory, "FrpDesk.exe"), overwrite: true);
        File.Copy(sourceFrpc, Path.Combine(runtimeDirectory, "frpc.exe"), overwrite: true);

        var serviceExecutable = Path.Combine(runtimeDirectory, "FrpDesk.exe");
        var serviceFrpc = Path.Combine(runtimeDirectory, "frpc.exe");
        var imagePath = $"\"{serviceExecutable}\" --service-run --profile-id {profileId} " +
            $"--frpc-path \"{serviceFrpc}\" --config-path \"{ConfigPath(profileId)}\" " +
            $"--log-path \"{LogPath(profileId)}\" --status-path \"{StatusPath(profileId)}\"";
        RunTool("sc.exe", false, "create", serviceName,
            "binPath=", imagePath,
            "type=", "own",
            "start=", "auto",
            "DisplayName=", $"FrpDesk instance {profileId}");

        try
        {
            RunTool("sc.exe", false, "sidtype", serviceName, "unrestricted");
            RunTool("sc.exe", false, "config", serviceName, "obj=", serviceAccount, "password=", "");
            RunTool("icacls.exe", false, runtimeDirectory, "/grant", $"{serviceAccount}:(OI)(CI)RX");
            GrantInstanceAccess(profileId, serviceAccount);
            RunTool("sc.exe", false, "failure", serviceName,
                "reset=", "86400",
                "actions=", "restart/60000/restart/120000/restart/240000");
            RunTool("sc.exe", false, "failureflag", serviceName, "1");
            RunTool("sc.exe", false, "start", serviceName);
            PruneServiceHosts(hostRoot);
        }
        catch
        {
            RemovePermissions(profileId, serviceAccount);
            RunTool("sc.exe", true, "delete", serviceName);
            throw;
        }
    }

    private static void StopAndRemove(string profileId)
    {
        var serviceName = ServiceName(profileId);
        var serviceAccount = "NT SERVICE\\" + serviceName;
        if (IsInstalled(serviceName))
        {
            try
            {
                using var controller = new ServiceController(serviceName);
                if (controller.Status != ServiceControllerStatus.Stopped)
                {
                    controller.Stop();
                    controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                }
            }
            catch (InvalidOperationException) { }

            RemovePermissions(profileId, serviceAccount);
            RunTool("sc.exe", true, "delete", serviceName);
            for (var index = 0; index < 20 && IsInstalled(serviceName); index++) Thread.Sleep(250);
        }
        RemoveLegacyTask(profileId);
        try
        {
            var runtimeDirectory = RuntimeDirectory(profileId);
            if (Directory.Exists(runtimeDirectory) && !IsRuntimeInUse(runtimeDirectory))
                Directory.Delete(runtimeDirectory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static void Restart(string profileId)
    {
        var serviceName = ServiceName(profileId);
        if (!IsInstalled(serviceName)) throw new InvalidOperationException("找不到该实例的 Windows 后台服务。");
        using var controller = new ServiceController(serviceName);
        if (controller.Status != ServiceControllerStatus.Stopped)
        {
            controller.Stop();
            controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
        }
        controller.Start();
        controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
    }

    private static void StopAndDeleteExisting(string serviceName)
    {
        try
        {
            using var controller = new ServiceController(serviceName);
            if (controller.Status != ServiceControllerStatus.Stopped)
            {
                controller.Stop();
                controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            }
        }
        catch (InvalidOperationException) { }
        RunTool("sc.exe", false, "delete", serviceName);
        for (var index = 0; index < 20 && IsInstalled(serviceName); index++) Thread.Sleep(250);
    }

    private static bool IsInstalled(string serviceName)
    {
        try
        {
            using var controller = new ServiceController(serviceName);
            _ = controller.Status;
            return true;
        }
        catch { return false; }
    }

    private static void SecureHostRoot(string hostRoot)
    {
        Directory.CreateDirectory(hostRoot);
        RunTool("icacls.exe", false, hostRoot, "/grant",
            "*S-1-5-18:(OI)(CI)F",
            "*S-1-5-32-544:(OI)(CI)F",
            "*S-1-5-32-545:(OI)(CI)RX");
    }

    private static void GrantInstanceAccess(string profileId, string serviceAccount)
    {
        var localRoot = LocalRoot();
        var instancesRoot = Path.Combine(localRoot, "instances");
        Directory.CreateDirectory(instancesRoot);
        RunTool("icacls.exe", false, localRoot, "/grant", $"{serviceAccount}:(X)");
        RunTool("icacls.exe", false, instancesRoot, "/grant", $"{serviceAccount}:(X)");
        RunTool("icacls.exe", false, InstanceDirectory(profileId), "/grant", $"{serviceAccount}:(OI)(CI)M", "/T", "/C");
    }

    private static void RemovePermissions(string profileId, string serviceAccount)
    {
        RunTool("icacls.exe", true, InstanceDirectory(profileId), "/remove", serviceAccount, "/T", "/C");
        RunTool("icacls.exe", true, Path.Combine(LocalRoot(), "instances"), "/remove", serviceAccount);
        RunTool("icacls.exe", true, LocalRoot(), "/remove", serviceAccount);
        RunTool("icacls.exe", true, RuntimeDirectory(profileId), "/remove", serviceAccount, "/T", "/C");
    }

    private static void RemoveLegacyTask(string profileId)
    {
        var taskName = ServicePrefix + profileId;
        RunTool("schtasks.exe", true, "/End", "/TN", taskName);
        RunTool("schtasks.exe", true, "/Delete", "/TN", taskName, "/F");
        var scriptPath = Path.Combine(InstanceDirectory(profileId), "frpc-background.cmd");
        try { if (File.Exists(scriptPath)) File.Delete(scriptPath); } catch { }
    }

    private static void PruneServiceHosts(string hostRoot)
    {
        foreach (var directory in Directory.GetDirectories(hostRoot))
        {
            try
            {
                if (IsRuntimeInUse(directory)) continue;
                var info = new DirectoryInfo(directory);
                if (info.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-7))
                    Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool IsRuntimeInUse(string runtimeDirectory)
    {
        var fullPath = Path.GetFullPath(runtimeDirectory);
        foreach (var service in ServiceController.GetServices())
        {
            if (!service.ServiceName.StartsWith(ServicePrefix, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Services\{service.ServiceName}");
                if (key?.GetValue("ImagePath") is string imagePath
                    && imagePath.Contains(fullPath, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        return false;
    }

    private static string LocalRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrpDesk");

    private static string InstanceDirectory(string profileId) =>
        Path.Combine(LocalRoot(), "instances", profileId);

    private static string RuntimeDirectory(string profileId) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "FrpDesk", "ServiceHosts", profileId);

    private static string ConfigPath(string profileId) => Path.Combine(InstanceDirectory(profileId), "frpc.toml");
    private static string LogPath(string profileId) => Path.Combine(InstanceDirectory(profileId), "frpc-background.log");
    private static string StatusPath(string profileId) => Path.Combine(InstanceDirectory(profileId), "frpc-background.status.json");
    private static string ServiceName(string profileId) => ServicePrefix + profileId;

    private static void RunTool(string fileName, bool allowFailure, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"无法启动 {fileName}。");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"Windows 服务操作超时（{fileName}）。");
        }
        var output = (outputTask.GetAwaiter().GetResult() + errorTask.GetAwaiter().GetResult()).Trim();
        if (process.ExitCode != 0 && !allowFailure)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(output)
                ? $"Windows 服务操作失败（{fileName}，代码 {process.ExitCode}）。"
                : output);
    }
}
