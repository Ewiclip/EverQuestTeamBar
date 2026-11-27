internal sealed partial class FloatingWorkspace
{
    private static void EditionWait(Func<bool> condition)
    {
        long until = Environment.TickCount64 + 5000;
        while (!condition() && Environment.TickCount64 < until) { Application.DoEvents(); Thread.Sleep(5); }
        StandaloneTests.Check(condition(), "Direct-input runtime fixture timed out.");
    }
    internal void EditionOneTapSelfTest()
    {
        static void Check(bool value, string message) => StandaloneTests.Check(value, message);
        long now = 10000; TapClock = () => now; ControlClock = () => now;
        var foreground = Native.GetForegroundWindow();
        Check(foreground != 0, "An interactive desktop is required to verify foreground preservation.");
        Start(); EditionWait(() => Ready);
        Check(surfaces.Count == 1 && surfaces[0].Heading == "TEAM BAR", "A bar-only session created a pause or resume control strip.");
        var bar = surfaces[0];
        void Edge(uint message)
        {
            var point = PointToClient(new(bar.Bounds.Left + (int)(40 * scale), bar.Bounds.Top + (int)(60 * scale)));
            Native.SendMessageW(Handle, message, message == 0x201 ? 1 : 0, (nint)((point.Y << 16) | (point.X & 0xffff)));
        }
        Edge(0x201); now += 60000; CheckDelivery(); Check(sequence == 0, "Holding a button sent input.");
        Edge(0x202); Check(sequence == 1 && busy, "The first click must send exactly one request without a resume command.");
        Edge(0x201); Edge(0x202); Check(sequence == 1, "A busy bar queued another click.");
        EditionWait(() => !busy);
        for (int i = 0; i < 100; i++) { now += 60000; CheckDelivery(); RefreshPlacement(); RefreshAppearance(); CheckIdentities(); }
        Edge(0x202); Check(sequence == 1, "Elapsed time or mouse-up without mouse-down sent another tap.");
        Edge(0x201); downForeground = -1; Edge(0x202);
        Check(sequence == 1, "A changed foreground identity allowed a pending click to send input.");
        Edge(0x201); downBounds.Offset(1, 0); Edge(0x202);
        Check(sequence == 1, "Changed button bounds allowed a pending click to send input.");
        Edge(0x201); CancelPendingInput("Held bar click cancellation");
        Check(cancelInFlight && !Capture && pressed is null && dragged is null && pressedAction == -1,
            "Cancellation retained a captured bar click.");
        Edge(0x202); Edge(0x201); Edge(0x202);
        Check(sequence == 1, "Pending cancellation allowed a bar click to send input.");
        EditionWait(() => !cancelInFlight);
        Edge(0x202); Check(sequence == 1, "Cancellation acknowledgement retained a pending release.");
        Edge(0x201); Edge(0x202); EditionWait(() => !busy);
        Check(sequence == 2 && Ready, "A fresh bar click after cancellation did not send exactly one tap.");
        Check(Native.GetForegroundWindow() == foreground, "Direct-input test changed foreground."); Close();
    }
    internal void EditionTimeoutSelfTest()
    {
        long now = 5000; ControlClock = () => now; Start(); EditionWait(() => Ready);
        CancelPendingInput("Fixture cancellation timeout");
        now += 2999; CheckDelivery(); StandaloneTests.Check(!Failed && cancelInFlight && sequence == 0, "Cancellation timeout fired early or sent input.");
        now++; CheckDelivery();
        StandaloneTests.Check(Failed && !connected && !Ready && !deliveryTimer.Enabled && !Visible,
            "Missing cancellation acknowledgement did not close the session.");
    }
}
