namespace TeamBar;

internal static partial class GameViewRuntimeTests
{
    private static void TestSlotSwapButtons(Rectangle area, Action<bool, string> check)
    {
        Rectangle[] slots = [new(area.Left, area.Top, 420, 310), new(area.Left + 420, area.Top, 250, 150),
            new(area.Left + 420, area.Top + 150, 250, 160)];
        using var first = new Source(Color.IndianRed, area.Location, framed: true);
        using var second = new Source(Color.SteelBlue, area.Location, framed: true);
        using var third = new Source(Color.Green, area.Location, framed: true);
        foreach (var game in new[] { first, second, third }) game.Open();
        AcquireFixtureFocus(first);
        var handles = new[] { first.Handle, second.Handle, third.Handle };
        var preset = new GameViewLayout("Swap buttons", GameViewKind.MainStrip, "fixture", false, ["One", "Two", "Three"])
            { MasterSlot = 0, AddSwapButton = true, AlwaysOnTop = true };

        static void Press(GameViewSession session, int slot, string name = "SwapAction") =>
            ((Button)session.SwapButtonAt(slot)!.Controls[name]!).PerformClick();

        foreach (bool match in new[] { false, true })
        {
            var layout = preset with { MatchMasterResolution = match };
            using var session = GameViewSession.StartForTest(layout, slots, handles);
            GameViewWindows.Focus(second.Handle);
            session.ApplyForTest(layout, slots, handles, background: true);
            check(session.SourceHandles.SequenceEqual(handles) && (session.SurfaceHandles[1] != second.Handle) == match &&
                second.VisibleFrame == slots[match ? 0 : 1],
                $"Focusing a button-only game keeps its seat and configured resolution (matching={match}).");
            var original = session.SwapButtonAt(1)!;
            var stale = session.CaptureSwapButtonClickForTest(1);
            var thirdButton = session.SwapButtonAt(2)!;
            session.ReadSwapButtonPosition = (_, _) => throw new InvalidDataException("Invalid fixture anchor.");
            bool rejected = false;
            try { session.ApplyForTest(layout, slots, handles, background: true); }
            catch (InvalidDataException) { rejected = true; }
            finally { session.ReadSwapButtonPosition = null; }
            check(rejected && session.IsActive && session.SourceHandles.SequenceEqual(handles) &&
                session.SwapButtonAt(1) == original && original.Visible,
                "A failed position read for a replacement profile preserves existing widgets and the live arrangement.");
            Press(session, 2, "SwapPositionLock");
            var light = FloatingThemes.ForAppTheme(AppearanceTheme.Light);
            var originalBounds = thirdButton.Bounds;
            session.SetSwapButtonTheme(light);
            check(thirdButton.Controls["SwapAction"]!.ForeColor == FloatingThemes.Color(light.Text) &&
                thirdButton.Controls["SwapPositionLock"]!.BackColor == FloatingThemes.Color(light.Bar) &&
                thirdButton.Bounds == originalBounds && !thirdButton.Locked,
                "Changing the Team Bar theme recolors an open swap button and preserves its unlocked position.");
            check(!thirdButton.Locked && slots[1].Contains(original.Bounds) && OwnerAt(Center(original.Bounds)) == original.Handle,
                "The slot button is visible and hit-testable on its own game.");
            bool hiddenWhileSizing = false;
            if (!match)
            {
                second.NextResizeEnd = () =>
                {
                    hiddenWhileSizing = session.SwapButtonAt(0)?.Visible == false && !original.Visible && thirdButton.Visible;
                };
            }
            Press(session, 1);
            check(session.SwapButtonAt(0)!.BackColor == FloatingThemes.Color(light.ButtonBorder),
                "A new occupant inherits the current swap button theme.");
            if (!match) check(hiddenWhileSizing, "Only the outgoing labels disappear while the held pictures show their incoming characters.");
            check(session.SourceHandles.SequenceEqual(new[] { second.Handle, first.Handle, third.Handle }) &&
                Native.GetForegroundWindow() == second.Handle && second.VisibleFrame == slots[0] &&
                thirdButton == session.SwapButtonAt(2) && thirdButton.Locked && session.SwapButtonAt(0)!.Text.Contains("Two") &&
                session.SwapButtonAt(1)!.Text.Contains("One") && original.IsDisposed,
                $"The explicit button exchanges only its game with the star, updates both names, and locks the sibling (matching={match}).");
            first.ResizeTrace = [];
            second.ResizeTrace = [];
            Press(session, 0);
            check(first.ResizeTrace.Count == 0 && second.ResizeTrace.Count == 0 && session.SourceHandles[0] == second.Handle,
                "Selecting the current starred game does not resize or exchange it.");
            if (!match)
            {
                first.RefuseResize = true;
                try { Press(session, 1); }
                finally { first.RefuseResize = false; }
                check(session.IsActive && session.SourceHandles[0] == second.Handle && session.SwapButtonAt(0)!.Visible &&
                    session.SwapButtonAt(1)!.Visible && thirdButton == session.SwapButtonAt(2) && thirdButton.Visible,
                    "A failed button swap restores both outgoing labels and leaves the unaffected button in place.");
            }
            Press(session, 1);
            stale();
            check(session.SourceHandles.SequenceEqual(handles), "A stale button callback cannot swap a replacement widget, even after swapping back.");
            var quick = layout with { SwapOnClick = true };
            session.ApplyForTest(quick, slots, handles);
            PreviewSelection(session, 1);
            check(session.SourceHandles[0] == second.Handle && session.SwapButtonAt(0)!.Text.Contains("Two"),
                "Quick Swap and explicit swap buttons can be used together.");
            var removed = session.SwapButtonAt(1)!;
            session.ApplyForTest(quick with { AddSwapButton = false }, slots, handles, background: true);
            check(removed.IsDisposed && session.SwapButtonAt(0) is null && session.SwapButtonAt(2) is null,
                "Disabling swap buttons removes every widget and preserves the quick-swap arrangement.");
        }

        nint[] partial = [0, second.Handle, third.Handle];
        using var vacant = GameViewSession.StartForTest(preset, slots, partial);
        check(vacant.SwapButtonAt(0) is null && vacant.SurfaceHandles.SequenceEqual(partial),
            "Button-only slots remain playable at native size until a game fills the vacant reference slot.");
        Press(vacant, 1);
        check(vacant.SourceHandles.SequenceEqual(new nint[] { second.Handle, 0, third.Handle }) &&
            vacant.SwapButtonAt(1) is null && vacant.SwapButtonAt(0)!.Text.Contains("Two") && second.VisibleFrame == slots[0],
            "A button can fill an empty starred slot, and its former slot releases the button.");
        vacant.ApplyForTest(preset, slots, handles, background: true);
        check(vacant.SourceHandles.SequenceEqual(new[] { second.Handle, first.Handle, third.Handle }) &&
            vacant.SwapButtonAt(1)!.Text.Contains("One"), "An arriving game gets a button without undoing the live swap.");
    }
}
