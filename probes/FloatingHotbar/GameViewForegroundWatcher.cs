using System.Windows.Threading;

namespace TeamBar;

internal sealed partial class GameViewSession
{
    private const uint ForegroundEvent = 3; // EVENT_SYSTEM_FOREGROUND
    private readonly Dispatcher foregroundDispatcher = Dispatcher.CurrentDispatcher;
    private readonly Native.WinEventProc foregroundCallback;
    private readonly nint foregroundHook;
    private DispatcherOperation? foregroundCheck;

    private bool IsPreviewSource(nint window)
    {
        if (window == 0 || ended || updating || plan is not { } current ||
            !(current.MasterOccupied || current.FixedMatching)) return false;
        for (int i = 0; i < previews.Length; i++)
            if (previews[i]?.Source == window && current.At(i) is { } client && client.Handle == window)
                return GameViewWindows.Available(window, client.Pid);
        return false;
    }

    private void OnForegroundChanged(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time)
    {
        // Out-of-context events arrive on the registering UI thread. Run the
        // restacking or swap after the callback returns, coalescing rapid focus changes.
        if (hook != foregroundHook || objectId != 0 || childId != 0 || foregroundCheck is not null ||
            !IsPreviewSource(window)) return;
        foregroundCheck = foregroundDispatcher.InvokeAsync(() =>
        {
            try
            {
                // A newer focus change, assignment, or preview click can have
                // superseded the notification before the UI thread gets here.
                if (IsPreviewSource(Native.GetForegroundWindow())) CheckWindows();
            }
            finally { foregroundCheck = null; }
        });
    }

    private void StopWatchingForeground()
    {
        if (foregroundHook != 0) Native.UnhookWinEvent(foregroundHook);
        foregroundCheck?.Abort();
    }

#if !TEAMBAR_PUBLIC_RELEASE
    internal void PauseLayoutMonitorForTest() => monitor.Stop();
#endif
}
