using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TeamBar;

internal sealed record WindowIdentificationTarget(int Number, GameSession Session);

// Windows-only visual identification. No game reader, input bridge, or synthetic
// input is involved. Labels are temporary, non-activating, and click-through.
internal sealed class WindowIdentification : IDisposable
{
    private readonly Dictionary<int, IdentificationBadge> badges = [];
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 200 };
    private readonly WindowIdentificationTarget[] targets;
    private readonly Action<string> report;
    private readonly Func<GameSession, Rectangle?> readFrame;
    private readonly Func<GameSession, (long Owner, Rectangle Frame)?>? viewSurface;
    private readonly Func<long> clock;
    private readonly long expires;
    private bool disposed;
    internal bool Active => !disposed;
    internal int VisibleCount => badges.Values.Count(b => b.IsShowing);
    internal nint[] Handles => badges.Values.Select(b => b.Handle).ToArray();

    internal WindowIdentification(WindowIdentificationTarget[] windows, Action<string> status,
        Func<GameSession, Rectangle?>? frameReader = null, Func<long>? time = null,
        Func<GameSession, (long Owner, Rectangle Frame)?>? viewSurface = null)
    {
        targets = windows; report = status; readFrame = frameReader ?? ReadFrame;
        this.viewSurface = viewSurface;
        clock = time ?? (() => Environment.TickCount64); expires = clock() + 8000;
        timer.Tick += (_, _) => Refresh();
        try { Refresh(); if (!disposed) timer.Start(); }
        catch { Dispose(); throw; }
    }

    internal void Refresh()
    {
        if (disposed) return;
        if (clock() >= expires) { Dispose(); report("Numbers hidden. Identify again whenever you need them."); return; }
        var unavailable = new List<int>();
        foreach (var target in targets)
        {
            Rectangle? frame = null;
            long owner = target.Session.Window;
            try
            {
                if (viewSurface?.Invoke(target.Session) is { } surface)
                    (owner, frame) = surface;
                else frame = readFrame(target.Session);
            }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException or System.ComponentModel.Win32Exception) { }
            if (frame is null)
            {
                if (badges.TryGetValue(target.Number, out var old)) old.HideBadge();
                unavailable.Add(target.Number); continue;
            }
            if (!badges.TryGetValue(target.Number, out var badge))
            {
                badge = new(target.Number.ToString(), $"Window {target.Number}", "Match this number in\nAssign characters");
                badges.Add(target.Number, badge);
            }
            // Do not put a label over a different client or the assignment dialog.
            // An obscured/minimized client is reported instead of mislabelled.
            var place = FindVisiblePlacement(owner, frame.Value, badge.BadgeSize);
            if (place is null) { badge.HideBadge(); unavailable.Add(target.Number); continue; }
            badge.Place(place.Value);
        }
        int seconds = Math.Max(1, (int)Math.Ceiling((expires - clock()) / 1000d));
        string status = VisibleCount > 0 ? $"Showing {VisibleCount} window number{(VisibleCount == 1 ? "" : "s")} · hides in {seconds}s."
            : "No unobscured game windows to label.";
        if (unavailable.Count > 0) status += " Window " + string.Join(", ", unavailable)
            + ": covered, minimized, or unavailable. Use its Identify button, or restore the game window.";
        report(status);
    }

    internal static Rectangle? ReadFrame(GameSession expected) => ReadFrame(expected, ClientSessions.ReadWindow);
    internal static Rectangle? ReadScreenFrame(GameSession expected) => ReadFrame(expected, ClientSessions.ReadScreenWindow);
    private static Rectangle? ReadFrame(GameSession expected, Func<int, GameSession> reader)
    {
        var actual = reader(expected.ProcessId);
        if (!SameSession(expected, actual) || !IsWindowVisible((nint)expected.Window) || IsIconic((nint)expected.Window)
            || !GetWindowRect((nint)expected.Window, out var rect)) return null;
        return Rectangle.Intersect(Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom), SystemInformation.VirtualScreen);
    }
    internal static bool SameSession(GameSession expected, GameSession actual) => expected.ProcessId == actual.ProcessId
        && expected.StartedUtcTicks == actual.StartedUtcTicks && expected.Window == actual.Window;

    internal static void Reveal(GameSession expected, nint assignmentWindow, Func<int, GameSession>? windowReader = null)
    {
        var actual = (windowReader ?? ClientSessions.ReadWindow)(expected.ProcessId);
        if (!SameSession(expected, actual)) throw new InvalidOperationException("This game session changed. Reopen Assign characters.");
        if (IsIconic((nint)expected.Window)) throw new InvalidOperationException("This game window is minimized. Restore it, then identify it again.");
        // Place the game directly behind the assignment dialog, keeping the
        // foreground window first instead of competing with it for HWND_TOP.
        const uint flags = 0x0001 | 0x0002 | 0x0010; // NOSIZE | NOMOVE | NOACTIVATE
        if (!SetWindowPos((nint)expected.Window, assignmentWindow, 0, 0, 0, 0, flags)) throw new InvalidOperationException("Could not bring this game window forward.");
    }

    internal static Rectangle? FindVisiblePlacement(long window, Rectangle frame, Size size)
    {
        if (frame.Width < size.Width || frame.Height < size.Height) return null;
        int pad = Math.Min(24, Math.Min((frame.Width - size.Width) / 2, (frame.Height - size.Height) / 2));
        int left = frame.Left + pad, right = frame.Right - size.Width - pad;
        int top = frame.Top + pad, bottom = frame.Bottom - size.Height - pad;
        foreach (var point in new[] { new Point(left, top), new(right, top), new(left, bottom), new(right, bottom),
            new(frame.Left + (frame.Width - size.Width) / 2, frame.Top + (frame.Height - size.Height) / 2) })
        {
            var area = new Rectangle(point, size);
            // A small grid also catches dialogs overlapping the middle of a label.
            bool clear = true;
            for (int x = 0; x < 3 && clear; x++) for (int y = 0; y < 3 && clear; y++)
            {
                var test = new Point(area.Left + 2 + x * (area.Width - 5) / 2, area.Top + 2 + y * (area.Height - 5) / 2);
                clear = GetAncestor(WindowFromPoint(test), 2).ToInt64() == window;
            }
            if (clear) return area;
        }
        return null;
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; timer.Stop(); timer.Dispose();
        foreach (var badge in badges.Values) badge.Dispose(); badges.Clear();
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
