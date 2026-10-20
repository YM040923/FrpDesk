using System.Threading;

namespace FrpDesk.Services;

public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showEvent;
    private readonly CancellationTokenSource _stop = new();

    public SingleInstance(string name)
    {
        _mutex = new Mutex(initiallyOwned: true, @"Local\FrpDesk-" + name, out var created);
        IsFirstInstance = created;
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\FrpDesk-Show-" + name);
        if (!created) _showEvent.Set();
        else _ = Task.Run(ListenForShowRequests);
    }

    public bool IsFirstInstance { get; }
    public event Action? ShowRequested;

    private void ListenForShowRequests()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                if (!_showEvent.WaitOne(500)) continue;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            ShowRequested?.Invoke();
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _showEvent.Dispose();
        if (IsFirstInstance)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        }
        _mutex.Dispose();
        _stop.Dispose();
    }
}
