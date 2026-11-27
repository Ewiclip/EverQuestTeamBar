using System.Windows.Threading;

namespace TeamBar;

// Setup signals only the process it will wait for. The event stays set during
// shutdown, including while a user-initiated exit is waiting for native cleanup.
internal sealed class InstallerExitSignal : IDisposable
{
    private readonly EventWaitHandle signal;
    private readonly Dispatcher dispatcher;
    private readonly Action requestExit;
    private RegisteredWaitHandle? registration;
    private bool disposed;

    internal static string Name(int processId) => $@"Local\TeamBar.Setup.Exit.{processId}";
    internal bool IsRequested => !disposed && signal.WaitOne(0);

    internal static InstallerExitSignal? Listen(Dispatcher dispatcher, Action requestExit)
    {
        // An unavailable event leaves the existing tray Exit command operational.
        try
        {
            var signal = new EventWaitHandle(false, EventResetMode.ManualReset, Name(Environment.ProcessId),
                new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = true }, out bool created);
            if (created) return new(signal, dispatcher, requestExit);
            signal.Dispose();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException) { }
        return null;
    }

    private InstallerExitSignal(EventWaitHandle signal, Dispatcher dispatcher, Action requestExit)
    {
        this.signal = signal;
        this.dispatcher = dispatcher;
        this.requestExit = requestExit;
        Arm();
    }

    private void Arm()
    {
        registration = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) =>
        {
            _ = dispatcher.InvokeAsync(() => { if (IsRequested && !dispatcher.HasShutdownStarted) requestExit(); });
        }, null, Timeout.Infinite, executeOnlyOnce: true);
    }

    // The app can decline to close if a settings edit cannot be saved.
    internal void AllowRetry()
    {
        if (!IsRequested) return;
        registration?.Unregister(null);
        signal.Reset();
        Arm();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        registration?.Unregister(null);
        signal.Dispose();
    }
}
