using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TeamBar;

internal static class GameViewWindows
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { internal int Left, Top, Right, Bottom; }

    // dwmapi.h uses pshpack1, including the two Win32 BOOLs following the opacity byte.
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct ThumbnailProperties
    {
        internal uint Flags;
        internal Rect Destination, Source;
        internal byte Opacity;
        internal int Visible, ClientAreaOnly;
    }

    internal static bool SameOwner(nint h, int pid) =>
        h != 0 && GetWindowThreadProcessId(h, out uint owner) != 0 && owner == (uint)pid;
    internal static bool Minimized(nint h) => IsIconic(h);
    internal static bool HasOpenDialog(nint h, int pid) =>
        SameOwner(h, pid) && !IsIconic(h) && !IsWindowEnabled(h);
    internal static bool Available(nint h, int pid) => SameOwner(h, pid) && GetAncestor(h, 2) == h &&
        IsWindowVisible(h) && !IsIconic(h) && IsWindowEnabled(h);

    internal static Size SourceSize(nint h, int pid)
    {
        if (!Available(h, pid))
            throw new InvalidOperationException("A game window closed, was minimized, or has a dialog open. Restore it and apply the layout again.");
        if (!GetClientRect(h, out var r)) throw WinError("Could not measure the game window");
        if (r.Right <= 0 || r.Bottom <= 0) throw new InvalidOperationException("A game window has no visible client area.");
        return new(r.Right, r.Bottom);
    }

    internal static nint Register(nint tile, nint source)
    {
        CheckDwm(DwmRegisterThumbnail(tile, source, out var thumb));
        return thumb;
    }

    internal static Rectangle ThumbnailBounds(Size surface, Rectangle frame, Rectangle visibleFrame, Rectangle client)
    {
        // DWM can supply the entire window bitmap, the painted frame, or just the client.
        if (surface == frame.Size && frame.Contains(client)) return frame;
        if (surface == client.Size) return client;
        if (surface == visibleFrame.Size && visibleFrame.Contains(client)) return visibleFrame;
        throw new InvalidOperationException($"Windows returned incompatible thumbnail bounds. Surface {surface}, window {frame}, visible frame {visibleFrame}, client {client}.");
    }

    internal static Size ThumbnailSize(nint thumb)
    {
        CheckDwm(DwmQueryThumbnailSourceSize(thumb, out var size));
        return size;
    }

    internal static void UpdateThumbnail(nint thumb, Rectangle destination, Rectangle? sourceCrop)
    {
        var properties = new ThumbnailProperties
        {
            Flags = 0x01 | 0x04 | 0x08 | 0x10,
            Destination = new() { Left = destination.Left, Top = destination.Top, Right = destination.Right, Bottom = destination.Bottom },
            Opacity = 255, Visible = 1, ClientAreaOnly = 0
        };
        if (sourceCrop is { } crop)
        {
            properties.Flags |= 0x02; // DWM_TNP_RECTSOURCE, relative to the source bitmap
            properties.Source = new() { Left = crop.Left, Top = crop.Top, Right = crop.Right, Bottom = crop.Bottom };
        }
        CheckDwm(DwmUpdateThumbnailProperties(thumb, ref properties));
    }

    internal static void Unregister(nint thumb) => DwmUnregisterThumbnail(thumb);
    internal static bool SourceLinkAlive(nint thumb) => thumb != 0 && DwmQueryThumbnailSourceSize(thumb, out _) >= 0;
    internal static void FlushComposition() => CheckDwm(DwmFlush());

    internal static void ConfigureTile(nint tile)
    {
        const int transitionsForcedDisabled = 3, cornerPreference = 33, borderColor = 34;
        int disabled = 1, doNotRound = 1, noBorder = unchecked((int)0xfffffffe);
        DwmSetWindowAttribute(tile, transitionsForcedDisabled, ref disabled, sizeof(int));
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            DwmSetWindowAttribute(tile, cornerPreference, ref doNotRound, sizeof(int));
            DwmSetWindowAttribute(tile, borderColor, ref noBorder, sizeof(int));
        }
    }

    private static void CheckDwm(int hr)
    {
        if (hr < 0) throw new InvalidOperationException($"Windows could not display the live game view (0x{hr:X8}).", Marshal.GetExceptionForHR(hr));
    }

    internal static bool TryFocus(nint h) => Native.GetForegroundWindow() == h || SetForegroundWindow(h);

    internal static bool StillOwnsForeground(nint original)
    {
        // Between cross-thread deactivation and activation Windows may briefly return null.
        // Only wait through that gap. A different nonzero foreground immediately wins.
        var active = Native.GetForegroundWindow();
        long until = Environment.TickCount64 + 60;
        while (active == 0 && original != 0 && Environment.TickCount64 < until)
        {
            Thread.Sleep(3);
            active = Native.GetForegroundWindow();
        }
        return active == original;
    }

    internal static bool FocusForPreviewClick(nint window, int pid)
    {
        if (!Available(window, pid) || !TryFocus(window)) return false;
        return PreviewReceiverResponded(window, pid) && Native.GetForegroundWindow() == window;
    }

    internal static bool PreviewReceiverResponded(nint window, int pid)
    {
        // Cross-thread foreground activation can still be pending when SetForegroundWindow returns.
        // Wait for the target to process that activation before checking its focus and z-order.
        const uint abortIfHungOrExited = 0x0002 | 0x0020;
        return SameOwner(window, pid) && SendMessageTimeoutW(window, 0, 0, 0, abortIfHungOrExited, 300, out _) != 0 &&
            Available(window, pid);
    }

    internal static void Focus(nint h)
    {
        if (!TryFocus(h))
            throw new InvalidOperationException("Windows declined to focus the selected game window. Activate the game and apply the layout again.");
    }

    internal static void Place(nint h, Rectangle r)
    {
        const uint keepActivationAndOrder = 0x0010 | 0x0200 | 0x0004;
        if (!Native.SetWindowPos(h, 0, r.X, r.Y, r.Width, r.Height, keepActivationAndOrder))
            throw WinError("Could not place the game view");
    }

    private static Exception WinError(string context) => new InvalidOperationException(
        $"{context}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");

    [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(nint destination, nint source, out nint thumbnail);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int bytes);
    [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(nint thumbnail, ref ThumbnailProperties properties);
    [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(nint thumbnail);
    [DllImport("dwmapi.dll")] private static extern int DwmQueryThumbnailSourceSize(nint thumbnail, out Size size);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint h);
    [DllImport("user32.dll")] private static extern nint SendMessageTimeoutW(nint window, uint message, nuint wparam,
        nint lparam, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint h, out uint pid);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint h, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint h);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint h);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint h);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetClientRect(nint h, out Rect rect);
}
