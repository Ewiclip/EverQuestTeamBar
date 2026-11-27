namespace TeamBar;

internal static partial class GameViewRuntimeTests
{
    private static void TestLayoutTitleBars(Screen monitor, Rectangle area, Action<bool, string> check)
    {
        using var upper = new Source(Color.Teal, area.Location, framed: true);
        using var lower = new Source(Color.Maroon, new(area.Left + 60, area.Top + 30), framed: true);
        upper.Open();
        lower.Open();
        AcquireFixtureFocus(upper);
        Source[] games = [upper, lower];
        nint[] handles = [upper.Handle, lower.Handle];
        var first = new Rectangle(area.Left, area.Top, area.Width * 3 / 4, area.Height / 2);
        Rectangle[] slots = [first, new(first.Left, first.Bottom, first.Width * 2 / 3, first.Height * 3 / 4)];
        var standard = new GameViewLayout("Stacked title bars", GameViewKind.Rows, monitor.DeviceName, false, ["Upper", "Lower"])
            { MasterSlot = 0, MatchMasterResolution = false, AlwaysOnTop = true };
        var hidden = standard with { HideTitleBars = true };
        static Rectangle Client(Source game) => game.RectangleToScreen(game.ClientRectangle);
        static (Rectangle, Rectangle, nint, nint, (int, Rectangle)) State(Source game) =>
            (game.Bounds, Client(game), GetWindowLongPtrW(game.Handle, -16), GetWindowLongPtrW(game.Handle, -20), WindowShape(game.Handle));
        var original = games.Select(State).ToArray();

        using (var session = GameViewSession.StartForTest(standard, slots, handles))
        {
            bool BothCaptionsVisible() => games.Select((g, i) => g.VisibleFrame == slots[i] && Client(g).Top > slots[i].Top).All(v => v);
            check(BothCaptionsVisible(), "Existing layouts display both native captions by default.");
            session.ApplyForTest(hidden, slots, handles);
            void CheckNativeClients()
            {
                for (int i = 0; i < games.Length; i++)
                {
                    var game = games[i];
                    check(session.SurfaceHandles[i] == handles[i] && Client(game) == slots[i] && game.VisibleFrame == slots[i] &&
                        session.TryGetVisibleClientRect(handles[i], out var anchor) && anchor == slots[i],
                        $"Fixed slot {i + 1} places the real game client and its overlay anchor edge to edge.");
                }
            }
            CheckNativeClients();
            PumpUntil(() => AllEdgesVisible(session, slots), "The top and bottom painted client rows must meet across the seam.");
            int x = UncoveredCoordinate(slots[1].Width,
                n => new(slots[0].Left + n, slots[0].Bottom - 1), n => new(slots[1].Left + n, slots[1].Top));
            // Check actual desktop input on both sides with the neighboring game in front.
            void Click(Source game, Point point)
            {
                game.Input.Clear();
                NativeGesture(game, point);
                check(game.Input.Count == 2 && game.Input[0] == (LeftDown, point) && game.Input[1] == (LeftUp, point),
                    $"The native click reached {game.BackColor.Name} at client pixel {point}.");
            }
            Click(lower, new(x, 0));
            Click(upper, new(x, slots[0].Height - 1));
            check(session.SourceHandles.SequenceEqual(handles), "Both sides of the seam accept input without changing the fixed assignments.");

            var traces = games.Select(g => g.ResizeTrace = []).ToArray();
            session.ApplyForTest(hidden, slots, handles, background: true);
            check(traces.All(trace => trace.Count == 0), "A background reapply leaves correctly placed hidden-title clients at their current resolution.");
            foreach (var game in games) game.ResizeTrace = null;
            session.ApplyForTest(standard, slots, handles);
            check(BothCaptionsVisible(), "Turning the setting off restores both captions while the layout is active.");
            session.ApplyForTest(hidden, slots, handles);

            foreach (bool match in new[] { false, true })
            {
                var swap = hidden with { SwapOnClick = true, MatchMasterResolution = match };
                session.ApplyForTest(swap, slots, handles);
                for (int promotion = 0; promotion < 2; promotion++)
                {
                    var assignments = session.SourceHandles;
                    for (int i = 0; i < slots.Length; i++)
                    {
                        var source = games[Array.IndexOf(handles, assignments[i])];
                        var expected = match ? slots[0].Size : slots[i].Size;
                        bool located = session.TryGetVisibleClientRect(source.Handle, out var picture);
                        check(source.ClientSize == expected && located && picture == slots[i],
                            $"Slot {i + 1} displays the title-free client at render size {expected}, measured {source.ClientSize}. " +
                            $"Expected picture {slots[i]}, measured {picture} (available {located}).");
                    }
                    PumpUntil(() => AllEdgesVisible(session, slots), "A promoted game and its preview must both display every outer client edge.");
                    var promoted = games[Array.IndexOf(handles, assignments[1])];
                    PreviewSelection(session, 1);
                    check(session.SurfaceHandles[0] == promoted.Handle && Client(promoted) == slots[0],
                        "Promotion places the real client over the entire master slot.");
                    Click(promoted, new(slots[0].Width / 2, 0));
                }
            }
            AcquireFixtureFocus(upper);
            session.ApplyForTest(hidden with { MatchMasterResolution = true }, slots, handles);
            check(session.SurfaceHandles[1] != lower.Handle && lower.ClientSize == slots[0].Size,
                "A smaller fixed slot shows the full-size character UI while the master has focus.");
            PumpUntil(() => AllEdgesVisible(session, slots), "The fixed, scaled preview must fill every edge of its assigned slot.");
            lower.Input.Clear();
            var fixedPreview = session.SurfaceHandles[1];
            var topPixel = ClickFixedPreview(session, 1, new(slots[1].Left + slots[1].Width / 2, slots[1].Top));
            check(session.SurfaceHandles[1] == fixedPreview && Client(lower).Size == slots[0].Size &&
                session.SourceHandles.SequenceEqual(handles) && topPixel.Y == 0 && lower.Input.Count == 2 &&
                lower.Input.All(entry => AtRequestedPixel(entry.Point, topPixel)),
                "The first pixel row of a fixed preview reaches the game's top client row with its title hidden and resolution intact.");
            session.ApplyForTest(hidden, slots, handles);
            CheckNativeClients();
            var changed = new[] { slots[0] with { Height = slots[0].Height - 15 }, slots[1] with { Width = slots[1].Width + 21 } };
            lower.RefuseResize = true;
            bool refused = false;
            try { session.ApplyForTest(hidden, changed, handles); }
            catch (InvalidOperationException) { refused = true; }
            finally { lower.RefuseResize = false; }
            check(refused && session.IsActive, "An interrupted two-window resize keeps the previous layout active.");
            CheckNativeClients();
        }
        check(games.Select(State).SequenceEqual(original), "Ending the hidden-title layout restores both original native frames, client areas, styles and regions.");

        var entireMonitor = hidden with { Characters = ["Upper"] };
        using (var session = GameViewSession.StartForTest(entireMonitor, new[] { monitor.Bounds }, new[] { upper.Handle }))
        {
            check(Client(upper) == monitor.Bounds && upper.VisibleFrame == monitor.Bounds &&
                session.TryGetVisibleClientRect(upper.Handle, out var picture) && picture == monitor.Bounds,
                "The full-monitor slot includes the taskbar strip and first client row with a native captioned game.");
        }
        check(State(upper) == original[0], "Ending the full-monitor layout restores the original window and its visible caption.");
    }
}
