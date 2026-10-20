using FrpDesk.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace FrpDesk;

public partial class App : Microsoft.UI.Xaml.Application
{
    private SingleInstance? _singleInstance;
    private MainWindow? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var commandLine = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (WindowsServiceAdmin.TryRun(commandLine) || ServiceRuntime.TryRun(commandLine))
        {
            Exit();
            return;
        }

        _singleInstance = new SingleInstance("FrpDesk");
        if (!_singleInstance.IsFirstInstance)
        {
            Exit();
            return;
        }

        _window = new MainWindow();
        var queue = DispatcherQueue.GetForCurrentThread();
        _singleInstance.ShowRequested += () => queue.TryEnqueue(_window.ShowFromTray);
        _window.Closed += (_, _) => _singleInstance.Dispose();

        if (StartupRegistration.ShouldStartMinimized(commandLine)) _window.StartHidden();
        else _window.Activate();
    }
}
