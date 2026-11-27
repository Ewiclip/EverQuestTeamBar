internal sealed partial class FloatingWorkspace
{
    private bool cancelInFlight;
    private long controlPendingSince;
    internal Func<long> ControlClock = () => Environment.TickCount64;

    // Cancellation invalidates only the current gesture/request, never future clicks.
    private void ClearCapturedClick()
    {
        pressed = dragged = null; pressedAction = -1; Capture = false;
    }
    private void CancelPendingInput(string source)
    {
        remainingTaps.Clear();
        ClearCapturedClick();
        if (busy) userCancelledTap = sequence;
        if (!connected || cancelInFlight) return;
        controlPendingSince = ControlClock(); cancelInFlight = true;
        try { backend!.Send("cancel"); }
        catch (InvalidOperationException error) { FailSession("Controller unavailable · " + error.Message); }
        Write("input_cancelled", new { source }); Invalidate();
    }
    private void ReceiveCancelled()
    {
        remainingTaps.Clear();
        cancelInFlight = false;
        if (busy) userCancelledTap = sequence;
        ClearCapturedClick(); status = BuildEdition.ReadyHint;
    }
    private bool CheckControlTimeout()
    {
        if (!cancelInFlight || ControlClock() - controlPendingSince < 3000) return false;
        FailSession("The controller did not acknowledge cancellation. Reconnect input controls."); return true;
    }
}
