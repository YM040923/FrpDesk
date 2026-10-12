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

        base.OnStartup(e);
        new MainWindow().Show();
    }
}
