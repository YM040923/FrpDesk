using FrpDesk.Services;

namespace FrpDesk;

public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // 服务宿主和管理助手必须在 WinUI 初始化之前处理，否则 session 0 里的服务
        // 和隐藏的管理进程都会先尝试启动图形界面。
        if (WindowsServiceAdmin.TryRun(args) || ServiceRuntime.TryRun(args)) return;

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(_ =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }
}
