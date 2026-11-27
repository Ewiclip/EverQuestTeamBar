namespace TeamBar;

internal static partial class GameViewRuntimeTests
{
    private static void TestReplacingDroppedCrop(Rectangle area, Action<bool, string> check)
    {
        using var game = new Source(Color.DarkCyan, area.Location, framed: true);
        game.Open();
        int pid = Environment.ProcessId;
        nint h = game.Handle;
        var placements = new GameViewPlacements(false);
        var small = new Rectangle(area.Left + 24, area.Top + 36, 250, 160);
        var large = new Rectangle(area.Left + 42, area.Top + 42, 440, 310);
        try
        {
            var client = placements.PrepareSources([(h, pid)], ["Incoming"], [small], hideTitleBars: true)[0];
            var frame = GameViewPlacements.Bounds(h);
            check(!placements.CanReplaceDroppedSlotClip(h, pid, client), "An intact crop continues through normal verification.");
            if (SetWindowRgn(h, 0, true) == 0) throw new InvalidOperationException("Could not reproduce a lost fixture crop.");
            bool oldCropRejected = false;
            try { placements.VerifySlotClip(h, pid, "Incoming"); }
            catch (InvalidOperationException) { oldCropRejected = true; }
            check(oldCropRejected && placements.CanReplaceDroppedSlotClip(h, pid, client) &&
                !placements.CanReplaceDroppedSlotClip(h, pid, client with { Height = client.Height - 1 }),
                "A lost outgoing crop would fail ordinary repair and may be replaced only while the recorded client still matches.");
            using (var custom = new Region(new Rectangle(0, 0, 80, 80)))
            {
                custom.Exclude(new Rectangle(15, 15, 20, 20));
                game.Region = custom;
                check(!placements.CanReplaceDroppedSlotClip(h, pid, client), "A foreign custom region is never treated as a lost layout crop.");
                game.Region = null;
            }
            Native.SetWindowPos(h, 0, frame.Left + 5, frame.Top, 0, 0, 0x0215);
            check(!placements.CanReplaceDroppedSlotClip(h, pid, GameViewPlacements.ClientBounds(h, pid)),
                "A moved native frame stays subject to validation even when its current client is supplied.");
            Native.SetWindowPos(h, 0, frame.Left, frame.Top, 0, 0, 0x0215);
            check(GameViewSession.WillRefitAfterFocus(false, 0, 1, 0) && GameViewSession.WillRefitAfterFocus(false, 0, 1, 1) &&
                !GameViewSession.WillRefitAfterFocus(false, 0, 1, 2) && !GameViewSession.WillRefitAfterFocus(true, 0, 1, 1) &&
                !GameViewSession.WillRefitAfterFocus(false, -1, 1, 1) && !GameViewSession.WillRefitAfterFocus(false, 0, -1, 1),
                "Only the two occupied seats moving during a slot-resolution foreground swap can replace a departing crop.");
            check(placements.CanReplaceDroppedSlotClip(h, pid, client) &&
                placements.PrepareSources([(h, pid)], ["Incoming"], [large], hideTitleBars: true)[0] == large &&
                placements.MatchesActiveSlot(h, large), "The incoming game receives its new crop and exact native master geometry.");
        }
        finally { placements.Restore(); }
    }

    private static void TestForegroundSwapAfterCropLoss(Rectangle area, Action<bool, string> check)
    {
        var slots = new[] { new Rectangle(area.Left, area.Top, 420, 300),
            new Rectangle(area.Left + 420, area.Top, 240, 150), new Rectangle(area.Left + 420, area.Top + 150, 240, 150) };
        var layout = new GameViewLayout("Foreground crop", GameViewKind.MainStrip, "fixture", true, ["Master", "Incoming", "Other"])
            { HideTitleBars = true, MatchMasterResolution = false, MasterSlot = 0 };
        using var master = new Source(Color.DarkBlue, area.Location, framed: true);
        using var incoming = new Source(Color.DarkCyan, area.Location, framed: true);
        using var other = new Source(Color.Tan, area.Location, framed: true);
        foreach (var game in new[] { master, incoming, other }) game.Open();
        AcquireFixtureFocus(master);
        using var session = GameViewSession.StartForTest(layout, slots, [master.Handle, incoming.Handle, other.Handle]);
        session.PauseLayoutMonitorForTest();
        var otherClip = WindowShape(other.Handle);
        if (SetWindowRgn(incoming.Handle, 0, true) == 0) throw new InvalidOperationException("Could not reproduce the incoming crop loss.");
        AcquireFixtureFocus(incoming);
        PumpUntil(() => !session.IsActive || session.SourceHandles[0] == incoming.Handle, "Foreground activation did not promote the fixture.");
        check(session.IsActive && GameViewPlacements.ClientBounds(incoming.Handle, Environment.ProcessId) == slots[0] &&
            GameViewPlacements.ClientBounds(master.Handle, Environment.ProcessId) == slots[1] && WindowShape(other.Handle) == otherClip,
            $"A foreground event swaps the two native clients despite the discarded departing crop and keeps the third seat intact. {session.EndReason}");
    }
}
