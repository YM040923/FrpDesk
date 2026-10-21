using FrpDesk.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace FrpDesk;

public partial class App : Microsoft.UI.Xaml.Application
{
    private SingleInstance? _singleInstance;
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => WriteCrashLog(e.Exception?.ToString() ?? e.Message);
    }

    private static void WriteCrashLog(string text)
    {
        try
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "FrpDesk-crash.txt"), text);
        }
        catch
        {
            // 崩溃日志只是诊断辅助，写不进去不影响退出流程。
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var commandLine = Environment.GetCommandLineArgs().Skip(1).ToArray();
        _singleInstance = new SingleInstance("FrpDesk");
        if (!_singleInstance.IsFirstInstance)
        {
            Exit();
            return;
        }

        try
        {
            _window = new MainWindow();
        }
        catch (Exception exception)
        {
            WriteCrashLog(exception.ToString());
            throw;
        }

        var queue = DispatcherQueue.GetForCurrentThread();
        _singleInstance.ShowRequested += () => queue.TryEnqueue(_window.ShowFromTray);
        _window.Closed += (_, _) => _singleInstance.Dispose();

        if (StartupRegistration.ShouldStartMinimized(commandLine)) _window.StartHidden();
        else _window.Activate();
    }
}
