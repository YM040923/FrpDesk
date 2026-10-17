using System.Windows;
using Application = System.Windows.Application;

namespace FrpDesk;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (Services.WindowsServiceAdmin.TryRun(e.Args))
        {
            Shutdown(Environment.ExitCode);
            return;
        }

        if (Services.ServiceRuntime.TryRun(e.Args))
        {
            Shutdown();
            return;
        }

        // A version update moves the executable, so rewrite the Run key before the UI opens.
        try
        {
            var settings = new Services.AppSettingsStore().Load();
            if (settings.AutoStart) Services.StartupRegistration.Apply(true);
        }
        catch
        {
            // Auto-start is optional; a failed Run key write must not block startup.
        }

        var startMinimized = Services.StartupRegistration.ShouldStartMinimized(e.Args);
        base.OnStartup(e);
        var window = new MainWindow();
        if (startMinimized) window.WindowState = WindowState.Minimized;
        window.Show();
    }
}
