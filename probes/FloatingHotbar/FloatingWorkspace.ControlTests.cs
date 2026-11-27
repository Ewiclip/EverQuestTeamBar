using System.Text.Json;

internal sealed partial class FloatingWorkspace
{
    internal void ControlRaceSelfTest()
    {
        static void Check(bool value, string message) => StandaloneTests.Check(value, message);
        string sessionProfile = surfaces[0].Path + ".session-" + Guid.NewGuid().ToString("N");
        SpellPipeName = "TeamBar-ClearHotkeysTest-" + Guid.NewGuid().ToString("N");
        using var session = new BarSession(sessionProfile, () => Ready, () => BeginInvoke(() => Close()), ClearHotkeys,
            () => Volatile.Read(ref identityBlocked));
        Start(); EditionWait(() => Ready);
        Check(tray is { Visible: true } && trayMenu!.Items.OfType<ToolStripMenuItem>().Any(item => item.Text == "Close floating buttons"),
            "Saved hotkey surfaces have no accessible exit action.");
        // Keep fixture replies queued while exercising the cancellation gate. The
        // fixture controller has no game-input implementation.
        CancelPendingInput("Cancellation race fixture");
        CancelPendingInput("Repeated cancellation fixture");
        Check(cancelInFlight && Ready && sequence == 0, "Repeated cancellation sent input or disconnected the ready session.");
        EditionWait(() => !cancelInFlight);
        Check(!cancelInFlight && Ready && sequence == 0 && !Capture && pressed is null && dragged is null,
            "Cancellation acknowledgement did not clear captured input while keeping the session ready.");
        ReceiveCancelled();
        Receive(JsonSerializer.SerializeToElement(new { type = "paused" }));
        Receive(JsonSerializer.SerializeToElement(new { type = "resumed" }));
        Check(!cancelInFlight && Ready && sequence == 0, "Retired or duplicate control replies changed readiness or sent input.");
        preferenceError = true;
        RefreshAppearance(); Check(!preferenceError, "A successful settings reload left a stale warning.");
        nint originalHandle = Handle; var bar = surfaces[0];
        var point = PointToClient(new(bar.Bounds.Left + (int)(40 * scale), bar.Bounds.Top + (int)(60 * scale)));
        nint packedPoint = (nint)((point.Y << 16) | (point.X & 0xffff));
        Native.SendMessageW(Handle, 0x201, 1, packedPoint);
        Check(pressed == bar && Capture, "Clear-hotkeys fixture did not capture its original button.");
        var clearing = BarSession.ClearHotkeys(sessionProfile); EditionWait(() => clearing.IsCompleted);
        Check(clearing.GetAwaiter().GetResult() && Ready && Handle == originalHandle && !Visible && surfaces.Count == 0,
            "Clearing hotkeys through the session did not retain a ready, hidden input host.");
        Check(tray is { Visible: true } && trayMenu is not null, "Clearing hotkeys removed the shared host's explicit exit action.");
        Check(!Capture && pressed is null && dragged is null && pressedAction == -1, "Clearing hotkeys retained a captured button.");
        Native.SendMessageW(Handle, 0x202, 0, packedPoint);
        Native.SendMessageW(Handle, 0x201, 1, packedPoint);
        Native.SendMessageW(Handle, 0x202, 0, packedPoint);
        using var pixel = new Bitmap(1, 1);
        using var graphics = Graphics.FromImage(pixel);
        for (int i = 0; i < 20; i++)
        {
            RefreshPlacement(); RefreshAppearance(); CheckIdentities(); Application.DoEvents();
            Check(Ready && Handle == originalHandle && !Visible && !IsWindowVisible(originalHandle)
                && Region is not null && Region.IsEmpty(graphics) && sequence == 0,
                "Cleared hotkeys returned after refresh, recreated their host or allowed an old button to send input.");
        }
        var spellStatus = TeamBar.SpellOverlayInputPipe.RequestNamed(new("status"), SpellPipeName);
        EditionWait(() => spellStatus.IsCompleted);
        Check(spellStatus.GetAwaiter().GetResult() is { Ok: true, Paused: false, Session: not null },
            "Clearing hotkeys disconnected the shared spell-input pipe.");
        // A cleared shared host can later become stale. Applying the empty library
        // must acknowledge success before closing it so spell input can reconnect.
        ReadIdentity = expected => expected with { Character = new("Different", 2, 130) };
        using var clearPipe = new System.IO.Pipes.NamedPipeClientStream(".", BarSession.Name(sessionProfile),
            System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
        var connecting = clearPipe.ConnectAsync(1000); EditionWait(() => connecting.IsCompleted); connecting.GetAwaiter().GetResult();
        using var reader = new StreamReader(clearPipe);
        using var writer = new StreamWriter(clearPipe) { NewLine = "\n", AutoFlush = true };
        async Task<BarState?> ReadClearResponse()
        {
            await writer.WriteLineAsync("clear-hotkeys");
            return JsonSerializer.Deserialize<BarState>(await reader.ReadLineAsync() ?? "null");
        }
        var clearResponse = ReadClearResponse(); EditionWait(() => clearResponse.IsCompleted);
        Check(clearResponse.GetAwaiter().GetResult() is { Cleared: true } && identityBlocked && !IsDisposed,
            "Clearing stale input failed to acknowledge the empty library before closing.");
        Application.DoEvents();
        Check(!IsDisposed && tray is { Visible: true }, "The stale host closed before the clear acknowledgment was received.");
        var received = writer.WriteLineAsync("received"); EditionWait(() => received.IsCompleted); received.GetAwaiter().GetResult();
        EditionWait(() => IsDisposed);
        Check(!IsHandleCreated && tray is null && spellInputPipe is null && sequence == 0,
            "The stale cleared host retained its controller window or spell pipe after acknowledgment.");
        Write("control_race_pass", new { gameInput = false, cancellationKeepsReady = true, retiredRepliesIgnored = true,
            hotkeysCleared = true, spellInputRetained = true, staleHostClosedAfterAcknowledgment = true });
    }
    internal void ControlTimeoutSelfTest()
    {
        static void Check(bool value, string message) => StandaloneTests.Check(value, message);
        long now = 10000; ControlClock = () => now; Start(); EditionWait(() => Ready);
        CancelPendingInput("Missing cancellation acknowledgement fixture");
        now += 2999; CheckDelivery();
        Check(!Failed && cancelInFlight && sequence == 0, "Cancellation timeout fired too early or sent input.");
        now++; CheckDelivery();
        Check(Failed && !connected && !Ready && !deliveryTimer.Enabled && !Visible,
            "Unacknowledged cancellation must close the shared input session.");
        Write("control_timeout_pass", new { gameInput = false, cancellation = true });
    }
    internal void TapTimeoutSelfTest()
    {
        static void Check(bool value, string message) => StandaloneTests.Check(value, message);
        long now = 5000; TapClock = () => now; Start(); EditionWait(() => Ready);
        // Model a missing reply without dispatching a command.
        busy = true; pendingSince = now;
        now += 2999; CheckDelivery(); Check(!Failed && sequence == 0, "Tap timeout fired too early or sent input.");
        now++; CheckDelivery();
        Check(Failed && !connected && !Ready && !deliveryTimer.Enabled && !Visible, "A missing tap acknowledgement did not close the input session.");
        Write("tap_timeout_pass", new { gameInput = false });
    }
    internal void StandaloneDirectInputSelfTest()
    {
        static void Check(bool value, string message) => StandaloneTests.Check(value, message);
        long now = 10000; TapClock = () => now; ControlClock = () => now;
        var foreground = Native.GetForegroundWindow();
        Start(); EditionWait(() => Ready);
        var standalone = surfaces.Single(s => s.Condition is not null);
        void Edge(uint message)
        {
            var point = PointToClient(new(standalone.Bounds.Left + (int)(40 * scale), standalone.Bounds.Top + (int)(60 * scale)));
            Native.SendMessageW(Handle, message, message == 0x201 ? 1 : 0, (nint)((point.Y << 16) | (point.X & 0xffff)));
        }
        Edge(0x201); now += 60000; CheckDelivery(); Check(sequence == 0, "Holding a standalone button sent input.");
        Edge(0x202); Check(sequence == 1 && busy, "The first standalone release must issue one direct tap without a resume command.");
        Edge(0x201); Edge(0x202); Check(sequence == 1, "A busy standalone button queued another tap.");
        EditionWait(() => !busy);
        for (int i = 0; i < 100; i++)
        {
            now += 60000; CheckDelivery(); RefreshPlacement(); RefreshAppearance(); CheckIdentities();
        }
        Check(sequence == 1 && !busy, "Elapsed time or background refresh sent an unsolicited standalone tap.");
        Edge(0x201); CancelPendingInput("Held-click cancellation");
        Check(cancelInFlight && pressed is null && dragged is null && pressedAction == -1 && !Capture,
            "Cancellation retained a captured standalone click.");
        Edge(0x202); Edge(0x201); Edge(0x202);
        Check(sequence == 1, "Pending cancellation allowed a captured or new standalone release to send input.");
        EditionWait(() => !cancelInFlight);
        Edge(0x202); Check(sequence == 1, "Settled cancellation retained a captured click.");
        Edge(0x201); ReceiveCancelled();
        Edge(0x202); Check(sequence == 1, "An unsolicited cancellation acknowledgement retained a captured click.");
        Edge(0x201); Edge(0x202); EditionWait(() => !busy);
        Check(sequence == 2, "A new standalone click after cancellation failed to send exactly one tap.");
        ReadIdentity = expected => expected with { Character = new("Runa", 2, 130) };
        CheckIdentities(); Edge(0x201); Edge(0x202);
        Check(!identityBlocked && cancelInFlight && sequence == 2, "Persona switch did not cancel a stale standalone gesture.");
        EditionWait(() => !cancelInFlight);
        Edge(0x201); Edge(0x202); EditionWait(() => !busy);
        Check(sequence == 3, "A fresh standalone click after a persona switch still required refresh.");
        Check(Native.GetForegroundWindow() == foreground, "Standalone direct-input test changed foreground.");
        Write("standalone_direct_input_pass", new { gameInput = false, virtualElapsedMinutes = 100, clicks = sequence });
        Close();
    }
    internal void HiddenHostSelfTest()
    {
        static void Check(bool value, string message) => StandaloneTests.Check(value, message);
        Check(surfaces.Count == 0, "The spell-only fixture unexpectedly created a floating surface.");
        int shownEvents = 0; bool everVisible = Visible;
        Shown += (_, _) => shownEvents++;
        VisibleChanged += (_, _) => everVisible |= Visible;
        nint originalHandle = Handle; var foreground = Native.GetForegroundWindow();
        long now = 10000; TapClock = () => now; ControlClock = () => now;
        Start(); EditionWait(() => Ready);
        using var pixel = new Bitmap(1, 1);
        Check(tray is null, "A spell-only input host created an extra tray icon.");
        using var graphics = Graphics.FromImage(pixel);
        for (int i = 0; i < 20; i++)
        {
            now += 60000; CheckDelivery(); RefreshPlacement(); RefreshAppearance(); CheckIdentities(); Application.DoEvents();
            Check(Ready && IsHandleCreated && Handle == originalHandle, "Refreshing the hidden host lost readiness or recreated its handle.");
            Check(!Visible && !IsWindowVisible(originalHandle) && !everVisible && shownEvents == 0,
                "The zero-surface input host became visible or raised Shown.");
            Check(Region is not null && Region.IsEmpty(graphics), "The zero-surface input host retained a mouse region.");
        }
        var status = TeamBar.SpellOverlayInputPipe.RequestNamed(new("status"), SpellPipeName);
        EditionWait(() => status.IsCompleted);
        Check(status.GetAwaiter().GetResult() is { Ok: true, Paused: false, Session: not null },
            "The hidden host did not expose a ready private spell-input session.");
        Check(sequence == 0 && !everVisible && shownEvents == 0 && Native.GetForegroundWindow() == foreground,
            "Starting or refreshing the hidden input host sent input, showed a window or changed foreground.");
        var wrongClose = TeamBar.SpellOverlayInputPipe.RequestNamed(new("close-spell-host", ProcessId: Environment.ProcessId + 1), SpellPipeName);
        EditionWait(() => wrongClose.IsCompleted);
        Check(wrongClose.GetAwaiter().GetResult() is { Ok: false } && !IsDisposed && Ready && Handle == originalHandle,
            "A close request for another process shut down the hidden host.");
        var close = TeamBar.SpellOverlayInputPipe.RequestNamed(new("close-spell-host", ProcessId: Environment.ProcessId), SpellPipeName);
        EditionWait(() => close.IsCompleted);
        Check(close.GetAwaiter().GetResult() is { Ok: true, Paused: false, Session: not null },
            "The hidden host closed before its successful close response was received.");
        EditionWait(() => IsDisposed);
        Check(!IsHandleCreated && !everVisible && shownEvents == 0, "Closing the hidden input host retained a handle or showed a window.");
        Write("hidden_host_pass", new { gameInput = false, stableHandle = true, shownEvents, emptyRegion = true, spellInputReady = true, closeAcknowledged = true });
    }
}
