namespace TeamBar;

internal static partial class GameViewRuntimeTests
{
    private sealed class PreviewReceiver : Form
    {
        internal volatile bool CheckActivation;
        internal volatile bool ActivatedThenAcknowledged;
        internal readonly ManualResetEventSlim ActivationStarted = new();
        private bool activated;

        protected override void WndProc(ref Message m)
        {
            if (CheckActivation && m.Msg == 6 && (m.WParam.ToInt64() & 0xffff) != 0) // WM_ACTIVATE
            {
                ActivationStarted.Set();
                Thread.Sleep(40); // Simulate a game restoring input devices on its own thread.
                activated = true;
            }
            if (CheckActivation && m.Msg == 0 && activated) ActivatedThenAcknowledged = true;
            base.WndProc(ref m);
        }
    }

    private static void TestPreviewActivation(Action<bool, string> check)
    {
        var ready = new TaskCompletionSource<(PreviewReceiver, nint)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var window = new PreviewReceiver { ShowInTaskbar = false };
            window.Shown += (_, _) => ready.SetResult((window, window.Handle));
            Application.Run(window);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var (receiver, handle) = ready.Task.WaitAsync(TimeSpan.FromSeconds(4)).GetAwaiter().GetResult();
        try
        {
            using var reference = new Form { ShowInTaskbar = false };
            reference.Show();
            receiver.CheckActivation = true;
            if (GameViewWindows.TryFocus(reference.Handle) && Native.GetForegroundWindow() == reference.Handle)
            {
                check(GameViewWindows.FocusForPreviewClick(handle, Environment.ProcessId) &&
                    receiver.ActivatedThenAcknowledged && Native.GetForegroundWindow() == handle,
                    "One focus request waits for a separate recipient thread to finish activation before the mapped gesture can start.");
            }
            else
            {
                // An inactive Windows desktop cannot become the foreground input desktop.
                // Exercise the same acknowledgment while its own window is processing activation.
                receiver.BeginInvoke(() => SendMessageW(handle, 6, 1, 0));
                check(receiver.ActivationStarted.Wait(2000) && GameViewWindows.PreviewReceiverResponded(handle, Environment.ProcessId) &&
                    receiver.ActivatedThenAcknowledged, "The input fence waits for the owned recipient's delayed activation handler.");
            }
            check(!GameViewWindows.FocusForPreviewClick(handle, -1), "A changed window owner cannot receive the gesture.");
        }
        finally
        {
            receiver.BeginInvoke(receiver.Close);
            if (!thread.Join(3000)) throw new TimeoutException("The owned preview input fixture did not stop.");
            receiver.ActivationStarted.Dispose();
        }
        var bounds = new Rectangle(0, 0, 3440, 1440);
        var desktop = new Rectangle(-3440, -1440, 10320, 2880);
        var origin = new Point(83, -240);
        var releaseTolerance = new Rectangle(81, -242, 4, 4);
        var jitter = new Point(origin.X + 1, origin.Y - 1);
        check(jitter != origin && GameViewSession.PreviewPointerProblem(releaseTolerance, jitter, 91, 91, 91, true, true) == null,
            "One pixel of natural movement during capture still accepts the original release on the same preview.");
        var rejected = new[]
        {
            GameViewSession.PreviewPointerProblem(releaseTolerance, new Point(85, -240), 91, 91, 91, true, true),
            GameViewSession.PreviewPointerProblem(releaseTolerance, jitter, 91, 91, 73, true, true),
            GameViewSession.PreviewPointerProblem(releaseTolerance, jitter, 91, 91, 91, false, true),
            GameViewSession.PreviewPointerProblem(releaseTolerance, jitter, 91, 91, 91, true, false)
        };
        check(rejected.All(reason => reason != null) && rejected.Distinct().Count() == rejected.Length &&
            GameViewSession.PreviewPointerProblem(releaseTolerance, jitter, 91, 73, 91, true, true) == rejected[1],
            "Moving beyond drag tolerance, a covered release or cursor position, a pressed button, and another mouse capture all decline with distinct reasons.");
        var center = new Point(1720, 720);
        check(GameViewSession.IsActivationWarp(center, origin, bounds, desktop, desktop, 105, 105) &&
            GameViewSession.IsActivationWarp(new Point(83, 0), origin, bounds, desktop, bounds, 105, 105),
            "A focus-induced center warp or newly confined edge can be recognized before pressing.");
        check(!GameViewSession.IsActivationWarp(center, origin, bounds, desktop, desktop, 105, 106) &&
            !GameViewSession.IsActivationWarp(center, origin, bounds, desktop, desktop, null, null) &&
            !GameViewSession.IsActivationWarp(new Point(83, 1), origin, bounds, desktop, bounds, 105, 105),
            "New input, unknown input timing and arbitrary pointer movement all cancel a proposed correction.");
    }
}
