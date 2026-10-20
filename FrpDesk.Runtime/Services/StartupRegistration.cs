using Microsoft.Win32;
using System.IO;

namespace FrpDesk.Services;

/// <summary>
/// Writes the per-user Run key so Windows launches FrpDesk after sign-in. Tunnels
/// keep running without the UI because each instance has its own Windows service.
/// </summary>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FrpDesk";

    /// <summary>Command line Windows runs at sign-in. Empty when auto-start is off.</summary>
    public static string? LaunchCommand()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) return null;
        return $"\"{executable}\" --{LaunchArgs.Minimized}";
    }

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        var value = key?.GetValue(ValueName) as string;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var executable = value.Trim();
        if (executable.StartsWith('"'))
        {
            var end = executable.IndexOf('"', 1);
            if (end > 1) executable = executable[1..end];
        }
        else
        {
            var space = executable.IndexOf(' ');
            if (space > 0) executable = executable[..space];
        }
        return File.Exists(executable);
    }

    /// <summary>
    /// Applies the auto-start choice and reports whether Windows accepted it.
    /// </summary>
    public static bool TryApply(bool enabled, out string? error)
    {
        error = null;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                error = "无法打开当前用户的启动项注册表。";
                return false;
            }

            if (!enabled)
            {
                if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName, throwOnMissingValue: false);
                return true;
            }

            var command = LaunchCommand();
            if (command is null)
            {
                error = "无法确认 FrpDesk 可执行文件路径。";
                return false;
            }
            key.SetValue(ValueName, command, RegistryValueKind.String);
            return true;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or IOException)
        {
            error = exception.Message;
            return false;
        }
    }

    /// <summary>Parses the sign-in launch arguments produced by <see cref="LaunchCommand"/>.</summary>
    public static bool ShouldStartMinimized(string[] args) =>
        args.Any(arg => arg.Equals("--" + LaunchArgs.Minimized, StringComparison.OrdinalIgnoreCase));

    private static class LaunchArgs
    {
        public const string Minimized = "minimized";
    }
}
