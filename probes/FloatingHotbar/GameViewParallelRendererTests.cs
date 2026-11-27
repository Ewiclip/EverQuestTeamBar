using System.Diagnostics;

namespace TeamBar;

internal static partial class GameViewRuntimeTests
{
    // Only sent to the HWND reported by an explicitly launched child fixture.
    private const int ArmRenderer = 0x8001, ReadRendererClock = 0x8002;

    private sealed class DelayedRenderer(Point origin) : Source(Color.SteelBlue, origin, framed: true)
    {
        private int delay;
        private bool reject;
        private long started, finished, nextResize;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == ArmRenderer)
            {
                delay = (int)m.WParam;
                reject = m.LParam != 0;
                started = finished = nextResize = 0;
                return;
            }
            if (m.Msg == ReadRendererClock)
            {
                m.Result = (nint)((int)m.WParam switch { 0 => started, 1 => finished, _ => nextResize });
                return;
            }
            if (m.Msg == EnterSizeMove && finished != 0 && nextResize == 0) nextResize = Stopwatch.GetTimestamp();
            int command = m.Msg == SystemCommand ? (int)m.WParam & 0xfff0 : 0;
            if (command == MaximizeCommand && delay > 0 && started == 0)
            {
                started = Stopwatch.GetTimestamp();
                Thread.Sleep(delay);
                if (reject)
                {
                    finished = Stopwatch.GetTimestamp();
                    return; // EQ accepts the message but never enters the maximized profile.
                }
            }
            base.WndProc(ref m);
            if (command == RestoreCommand && started != 0 && finished == 0) finished = Stopwatch.GetTimestamp();
        }
    }

    internal static void RunRendererFixture(Point location)
    {
        using var game = new DelayedRenderer(location);
        game.Open();
        Console.WriteLine(game.Handle.ToInt64());
        Console.Out.Flush();
        Application.Run(game);
    }

    private sealed class RendererProcess : IDisposable
    {
        private readonly Process process;
        internal nint Window { get; }
        internal int Pid => process.Id;

        internal RendererProcess(Point origin)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true };
            if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(GameViewRuntimeTests).Assembly.Location);
            foreach (var value in new[] { "--game-view-renderer-fixture", origin.X.ToString(), origin.Y.ToString() })
                start.ArgumentList.Add(value);
            process = Process.Start(start) ?? throw new IOException("Could not launch the renderer fixture.");
            try
            {
                var line = process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                if (!long.TryParse(line, out var handle) || !GameViewWindows.SameOwner((nint)handle, Pid))
                    throw new InvalidOperationException("The renderer fixture did not report its own window.");
                Window = (nint)handle;
            }
            catch { Dispose(); throw; }
        }

        internal void Arm(int milliseconds, bool reject = false) => SendMessageW(Window, ArmRenderer, milliseconds, reject ? 1 : 0);
        internal long Read(int index) => SendMessageW(Window, ReadRendererClock, index, 0).ToInt64();

        public void Dispose()
        {
            try
            {
                if (Window != 0 && GameViewWindows.SameOwner(Window, Pid)) Native.PostMessageW(Window, 0x0010, 0, 0); // WM_CLOSE
                if (!process.HasExited && !process.WaitForExit(2000)) { process.Kill(); process.WaitForExit(5000); }
            }
            finally { process.Dispose(); }
        }
    }

    private static void TestParallelRendererSwap(Rectangle area, Action<bool, string> check)
    {
        Rectangle[] home = [new(area.Left, area.Top, 470, 320), new(area.Left + 480, area.Top, 220, 160)];
        Rectangle[] swapped = [home[1], home[0]];
        using var first = new RendererProcess(home[0].Location);
        using var second = new RendererProcess(home[1].Location);
        var placements = new GameViewPlacements(false);
        Rectangle[] Fit(Rectangle[] seats, bool overlap = false) => placements.PrepareSources(
            [(first.Window, first.Pid), (second.Window, second.Pid)], ["First renderer", "Second renderer"], seats,
            overlapRendererRefreshes: overlap);
        double TimeSwap(bool overlap)
        {
            first.Arm(550);
            second.Arm(550);
            long start = Stopwatch.GetTimestamp();
            Fit(swapped, overlap);
            return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        double Overlap() => (Math.Min(first.Read(1), second.Read(1)) - Math.Max(first.Read(0), second.Read(0)))
            * 1000d / Stopwatch.Frequency;
        try
        {
            var originalClients = Fit(home);
            double serial = TimeSwap(false);
            check(first.Read(0) > 0 && second.Read(0) > 0 && first.Read(1) > first.Read(0) &&
                second.Read(1) > second.Read(0) && Overlap() <= 0,
                "Two separate game processes finished their normal renderer resets sequentially in the baseline.");
            Fit(home);
            double parallel = TimeSwap(true);
            double overlap = Overlap();
            var promotedClients = new[] { GameViewPlacements.ClientBounds(first.Window, first.Pid),
                GameViewPlacements.ClientBounds(second.Window, second.Pid) };
            check(overlap > 0 && promotedClients.SequenceEqual(originalClients.Reverse()),
                $"Independent renderer resets overlapped for {overlap:F0} ms and both native clients reached their destination resolution. " +
                $"Two 550 ms fixture resets took {serial:F0} ms sequentially and {parallel:F0} ms with overlap.");

            first.Arm(850);
            second.Arm(20, reject: true);
            bool rejected = false;
            try { Fit(home, overlap: true); }
            catch (InvalidOperationException ex) when (ex is not LayoutRollbackException) { rejected = true; }
            long returned = Stopwatch.GetTimestamp();
            check(rejected && second.Read(1) > 0 && first.Read(1) > second.Read(1) &&
                second.Read(2) >= first.Read(1) && returned >= first.Read(1) &&
                GameViewPlacements.ClientBounds(first.Window, first.Pid) == promotedClients[0] &&
                GameViewPlacements.ClientBounds(second.Window, second.Pid) == promotedClients[1],
                "A refused maximize waits for the other game's slow restore before rolling back both slots.");
        }
        finally { placements.Restore(); }
    }
}
