using System.Runtime.InteropServices;

// Route only gestures that begin on this bar to its private message queue.
// A dedicated hook thread does no painting, logging, IPC waits, or game work.
internal sealed class MouseShield : IDisposable
{
    internal const int RoutedMouse = 0x8000 + 77;
    private readonly nint window;
    private readonly Native.MouseProc callback;
    private readonly Thread thread;
    private readonly ManualResetEventSlim ready = new();
    private nint hook;
    private uint threadId, ownedButtons;
    private Exception? startupError;
    private long swallowed;
    private int postFault;
    internal long Swallowed => Interlocked.Read(ref swallowed);
    internal bool PostFault => Volatile.Read(ref postFault) != 0;

    internal MouseShield(nint target)
    {
        window = target; callback = Filter;
        thread = new Thread(Run) { IsBackground = true, Name = "Hotbar mouse interception" };
        thread.Start();
        ready.Wait();
        if (startupError is not null) { thread.Join(); ready.Dispose(); throw startupError; }
    }

    private void Run()
    {
        try
        {
            threadId = Native.GetCurrentThreadId();
            Native.PeekMessageW(out _, 0, 0, 0, 0); // Establish this thread's message queue.
            hook = Native.SetWindowsHookExW(14, callback, Native.GetModuleHandleW(null), 0);
            if (hook == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot intercept bar mouse clicks.");
        }
        catch (Exception error) { startupError = error; }
        finally { ready.Set(); }
        if (startupError is not null) return;
        try { Application.Run(); }
        finally { Native.UnhookWindowsHookEx(hook); }
    }

    private nint Filter(int code, nint wp, nint lp)
    {
        if (code < 0) return Native.CallNextHookEx(hook, code, wp, lp);
        var data = Marshal.PtrToStructure<Native.MouseHookData>(lp);
        int message = (int)wp;
        uint bit = message switch
        {
            0x201 or 0x202 => 1,
            0x204 or 0x205 => 2,
            0x207 or 0x208 => 4,
            0x20B or 0x20C => (data.MouseData >> 16) == 1 ? 8u : 16u,
            _ => 0
        };
        bool down = message is 0x201 or 0x204 or 0x207 or 0x20B;
        bool up = message is 0x202 or 0x205 or 0x208 or 0x20C;
        if (down && Native.WindowFromPoint(data.Point) == window)
        {
            if (!Route(message, data.Point)) return Native.CallNextHookEx(hook, code, wp, lp);
            ownedButtons |= bit;
        }
        else if (up && (ownedButtons & bit) != 0)
        {
            ownedButtons &= ~bit;
            Route(message, data.Point); // Swallow its matching up even if the UI has gone away.
        }
        else if ((message is 0x20A or 0x20E) && Native.WindowFromPoint(data.Point) == window)
        {
            // This preview has no wheel actions. Do not scroll the game beneath it.
        }
        else
        {
            if (message == 0x200 && (ownedButtons & 1) != 0) Route(message, data.Point);
            return Native.CallNextHookEx(hook, code, wp, lp);
        }
        Interlocked.Increment(ref swallowed);
        return 1;
    }

    private bool Route(int message, Native.Point point)
    {
        // Signed 16-bit screen coordinates follow the Windows mouse-message convention.
        nint packed = (nint)((uint)(ushort)point.X | ((uint)(ushort)point.Y << 16));
        bool posted = Native.PostMessageW(window, RoutedMouse, message, packed);
        if (!posted) Volatile.Write(ref postFault, 1);
        return posted;
    }

    public void Dispose()
    {
        Native.PostThreadMessageW(threadId, 0x12, 0, 0);
        thread.Join();
        ready.Dispose();
    }
}
