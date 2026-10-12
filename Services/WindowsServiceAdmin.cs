using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;

namespace FrpDesk.Services;

public sealed record WindowsServiceAdminRequest(
    string Action,
    string ServiceName,
    string SourceExecutable,
    string SourceFrpc,
    string RuntimeDirectory,
    string InstanceDirectory,
    string ConfigPath,
    string LogPath,
    string StatusPath,
    string ProfileId,
    string? ResultPath = null);

public static class WindowsServiceAdmin
{
    private const string LegacyTaskPrefix = "FrpDesk-Instance-";

    public static bool TryRun(string[] args)
    {
        if (args.Length < 2 || !args[0].Equals("--background-admin", StringComparison.OrdinalIgnoreCase)) return false;
        WindowsServiceAdminRequest? request = null;
        try
        {
            request = JsonSerializer.Deserialize<WindowsServiceAdminRequest>(Convert.FromBase64String(args[1]))
                ?? throw new InvalidDataException("后台服务参数无效。");
            ValidateRequest(request);
            if (request.Action == "install") InstallAndStart(request);
            else if (request.Action == "remove") StopAndRemove(request);
            else if (request.Action == "restart") Restart(request);
            else throw new InvalidDataException("不支持的服务操作。");
            return true;
        }
        catch (Exception exception)
        {
            if (!string.IsNullOrWhiteSpace(request?.ResultPath))
            {
                try { File.WriteAllText(request.ResultPath, exception.Message, new UTF8Encoding(false)); } catch { }
            }
            Environment.ExitCode = 1;
            return true;
        }
    }

    private static void ValidateRequest(WindowsServiceAdminRequest request)
    {
        if (!Guid.TryParseExact(request.ProfileId, "N", out var profileId)
            || request.ServiceName != LegacyTaskPrefix + profileId.ToString("N"))
            throw new InvalidDataException("实例标识无效。");

        static bool SamePath(string left, string right) => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

        var runningExecutable = Environment.ProcessPath ?? throw new InvalidDataException("无法确认服务宿主程序路径。");
        if (!SamePath(request.SourceExecutable, runningExecutable))
            throw new InvalidDataException("服务宿主路径与当前程序不一致。");
        var expectedFrpc = Path.Combine(Path.GetDirectoryName(runningExecutable)!, "assets", "frpc.exe");
        if (!SamePath(request.SourceFrpc, expectedFrpc) || !File.Exists(expectedFrpc))
            throw new InvalidDataException("内置 frpc 程序路径无效。");

        var expectedInstanceSuffix = Path.Combine("FrpDesk", "instances", profileId.ToString("N"));
        var instanceDirectory = Path.GetFullPath(request.InstanceDirectory);
        if (!instanceDirectory.EndsWith(expectedInstanceSuffix, StringComparison.OrdinalIgnoreCase)
            || !SamePath(request.ConfigPath, Path.Combine(instanceDirectory, "frpc.toml"))
            || !SamePath(request.LogPath, Path.Combine(instanceDirectory, "frpc-background.log"))
            || !SamePath(request.StatusPath, Path.Combine(instanceDirectory, "frpc-background.status.json")))
            throw new InvalidDataException("实例文件路径无效。");

        var serviceHostRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FrpDesk", "ServiceHosts");
        var runtimeDirectory = Path.GetFullPath(request.RuntimeDirectory);
        if (!runtimeDirectory.StartsWith(Path.TrimEndingDirectorySeparator(serviceHostRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || request.Action is not ("install" or "remove" or "restart"))
            throw new InvalidDataException("服务目录或操作类型无效。");

        var expectedResultPrefix = "frpc-admin-";
        if (request.ResultPath is null
            || !Path.GetFullPath(request.ResultPath).StartsWith(Path.TrimEndingDirectorySeparator(instanceDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(request.ResultPath).StartsWith(expectedResultPrefix, StringComparison.Ordinal))
            throw new InvalidDataException("服务操作结果路径无效。");
    }

    private static void InstallAndStart(WindowsServiceAdminRequest request)
    {
        RemoveLegacyTask(request);

        var serviceAccount = "NT SERVICE\\" + request.ServiceName;
        var serviceExecutable = Path.Combine(request.RuntimeDirectory, "FrpDesk.exe");
        var serviceFrpc = Path.Combine(request.RuntimeDirectory, "frpc.exe");
        var hostRoot = Path.GetDirectoryName(request.RuntimeDirectory)!;
        Directory.CreateDirectory(hostRoot);
        SecureHostRoot(hostRoot);
        Directory.CreateDirectory(request.RuntimeDirectory);
        if (!File.Exists(serviceExecutable)) File.Copy(request.SourceExecutable, serviceExecutable);
        if (!File.Exists(serviceFrpc)) File.Copy(request.SourceFrpc, serviceFrpc);

        if (IsInstalled(request.ServiceName)) StopAndDeleteExisting(request.ServiceName);

        var imagePath = $"\"{serviceExecutable}\" --service-run --profile-id {request.ProfileId} --frpc-path \"{serviceFrpc}\" --config-path \"{request.ConfigPath}\" --log-path \"{request.LogPath}\" --status-path \"{request.StatusPath}\"";
        RunTool("sc.exe", false, "create", request.ServiceName,
            "binPath=", imagePath,
            "type=", "own",
            "start=", "auto",
            "DisplayName=", $"FrpDesk instance {request.ProfileId}");

        try
        {
            RunTool("sc.exe", false, "sidtype", request.ServiceName, "unrestricted");
            RunTool("sc.exe", false, "config", request.ServiceName, "obj=", serviceAccount, "password=", "");
            RunTool("icacls.exe", false, request.RuntimeDirectory, "/grant", $"{serviceAccount}:(OI)(CI)RX");
            GrantInstanceAccess(request.InstanceDirectory, serviceAccount);
            RunTool("sc.exe", false, "failure", request.ServiceName,
                "reset=", "86400",
                "actions=", "restart/60000/restart/120000/restart/240000");
            RunTool("sc.exe", false, "failureflag", request.ServiceName, "1");
            RunTool("sc.exe", false, "start", request.ServiceName);
        }
        catch
        {
            RemovePermissions(request, serviceAccount);
            RunTool("sc.exe", true, "delete", request.ServiceName);
            throw;
        }
    }

    private static void StopAndRemove(WindowsServiceAdminRequest request)
    {
        var serviceAccount = "NT SERVICE\\" + request.ServiceName;
        if (IsInstalled(request.ServiceName))
        {
            try
            {
                using var controller = new ServiceController(request.ServiceName);
                if (controller.Status != ServiceControllerStatus.Stopped)
                {
                    controller.Stop();
                    controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                }
            }
            catch (InvalidOperationException) { }

            RemovePermissions(request, serviceAccount);
            RunTool("sc.exe", true, "delete", request.ServiceName);
        }
        RemoveLegacyTask(request);
    }

    private static void Restart(WindowsServiceAdminRequest request)
    {
        if (!IsInstalled(request.ServiceName)) throw new InvalidOperationException("找不到该实例的 Windows 后台服务。");
        using var controller = new ServiceController(request.ServiceName);
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
        RunTool("icacls.exe", false, hostRoot, "/inheritance:r", "/grant",
            "*S-1-5-18:(OI)(CI)F",
            "*S-1-5-32-544:(OI)(CI)F",
            "*S-1-5-32-545:(OI)(CI)RX");
    }

    private static void GrantInstanceAccess(string instanceDirectory, string serviceAccount)
    {
        var root = Path.GetPathRoot(instanceDirectory);
        var parent = Path.GetDirectoryName(instanceDirectory);
        while (!string.IsNullOrWhiteSpace(parent) && !string.Equals(parent, root, StringComparison.OrdinalIgnoreCase))
        {
            RunTool("icacls.exe", false, parent, "/grant", $"{serviceAccount}:(X)");
            parent = Path.GetDirectoryName(parent);
        }
        RunTool("icacls.exe", false, instanceDirectory, "/grant", $"{serviceAccount}:(OI)(CI)M", "/T", "/C");
    }

    private static void RemovePermissions(WindowsServiceAdminRequest request, string serviceAccount)
    {
        RunTool("icacls.exe", true, request.InstanceDirectory, "/remove", serviceAccount, "/T", "/C");
        var root = Path.GetPathRoot(request.InstanceDirectory);
        var parent = Path.GetDirectoryName(request.InstanceDirectory);
        while (!string.IsNullOrWhiteSpace(parent) && !string.Equals(parent, root, StringComparison.OrdinalIgnoreCase))
        {
            RunTool("icacls.exe", true, parent, "/remove", serviceAccount);
            parent = Path.GetDirectoryName(parent);
        }
        RunTool("icacls.exe", true, request.RuntimeDirectory, "/remove", serviceAccount, "/T", "/C");
    }

    private static void RemoveLegacyTask(WindowsServiceAdminRequest request)
    {
        var taskName = LegacyTaskPrefix + request.ProfileId;
        RunTool("schtasks.exe", true, "/End", "/TN", taskName);
        RunTool("schtasks.exe", true, "/Delete", "/TN", taskName, "/F");
        var scriptPath = Path.Combine(request.InstanceDirectory, "frpc-background.cmd");
        try { if (File.Exists(scriptPath)) File.Delete(scriptPath); } catch { }
    }

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
        process.WaitForExit();
        var output = (outputTask.GetAwaiter().GetResult() + errorTask.GetAwaiter().GetResult()).Trim();
        if (process.ExitCode != 0 && !allowFailure)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(output)
                ? $"Windows 服务操作失败（{fileName}，代码 {process.ExitCode}）。"
                : output);
    }
}
