using Microsoft.Win32;

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
        return !string.IsNullOrWhiteSpace(value);
    }

    /// <summary>
    /// Applies the auto-start choice. Failures are swallowed because a read-only or
    /// locked Run key must not stop the app from starting.
    /// </summary>
    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null) return;
            var command = enabled ? LaunchCommand() : null;
            if (command is null)
            {
                if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName, throwOnMissingValue: false);
                return;
            }

            key.SetValue(ValueName, command, RegistryValueKind.String);
        }
        catch (System.ComponentModel.Win32Exception) { }
        catch (UnauthorizedAccessException) { }
        catch (System.Security.SecurityException) { }
    }

    /// <summary>Parses the sign-in launch arguments produced by <see cref="LaunchCommand"/>.</summary>
    public static bool ShouldStartMinimized(string[] args) =>
        args.Any(arg => arg.Equals("--" + LaunchArgs.Minimized, StringComparison.OrdinalIgnoreCase));

    private static class LaunchArgs
    {
        public const string Minimized = "minimized";
    }
}
