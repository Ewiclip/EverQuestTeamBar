using System.Runtime.InteropServices;

namespace TeamBar;

internal static partial class GameViewRuntimeTests
{
    // Like a game hotbar, the marker has a fixed pixel size and follows a
    // viewport edge. A smaller native resolution reflows it; a thumbnail scales it.
    private static Rectangle HudMarker(Size client) => new(client.Width / 2 + 16, client.Height - 46, 26, 18);

    internal static void RunSwitchMatrix(string output)
    {
        var (_, area) = ClearFixtureArea(Environment.GetEnvironmentVariable("TEAMBAR_GAME_VIEW_FIXTURE_DISPLAY"));
        var splitDisplay = new Rectangle(area.X, area.Y, 350, 310);
        var fullDisplay = new Rectangle(area.X + 368, area.Y, 376, 310);
        const int master = 2, taskbarHeight = 24;
        var recipe = new GameViewLayout("Three seats", GameViewKind.EqualSplit, "split", true, ["One", "Two", "Three"])
        {
            Screens = [new("split", GameViewKind.EqualSplit, 2), new("full", GameViewKind.EqualSplit, 1)],
            MasterSlot = master
        };
        Rectangle[] Resolve(GameViewLayout layout) => GameViewGeometry.Slots(layout, name =>
        {
            var bounds = name == "split" ? splitDisplay : fullDisplay;
            return layout.AvoidTaskbar ? bounds with { Height = bounds.Height - taskbarHeight } : bounds;
        });
        using var first = new Source(Color.Firebrick, area.Location) { PaintHudMarker = true };
        using var second = new Source(Color.DarkGreen, area.Location) { PaintHudMarker = true };
        using var third = new Source(Color.SteelBlue, area.Location) { PaintHudMarker = true };
        Source[] games = [first, second, third];
        foreach (var game in games) game.Open();
        var handles = games.Select(g => g.Handle).ToArray();
        var original = games.Select(g => g.Bounds).ToArray();
        // The taskbar's topmost band is real. Its bounds stay within the isolated test area.
        using var taskbar = new Form { StartPosition = FormStartPosition.Manual, FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false, TopMost = true, Bounds = new(fullDisplay.Left, fullDisplay.Bottom - taskbarHeight, fullDisplay.Width, taskbarHeight) };
        using var ordinary = new Source(Color.Orchid, area.Location);
        taskbar.Show();
        ordinary.Open();
        ordinary.Bounds = new Rectangle(area.Left + 10, area.Bottom - 44, 72, 30);
        AcquireFixtureFocus(third);
        var results = new List<object>();
        var failures = new List<string>();
        var originalPointer = Cursor.Position;
        var parking = Center(ordinary.Bounds);
        GameViewSession? session = null;
        try
        {
            // Adjacent configurations differ by exactly one switch, beginning with the
            // reported working arrangement. Preserve the promoted seats between changes.
            for (int step = 0; step < 16; step++)
            {
                int flags = 7 ^ step ^ (step >> 1);
                bool swap = (flags & 1) != 0, match = (flags & 2) != 0;
                bool overlap = (flags & 4) != 0, pinned = (flags & 8) != 0;
                var layout = recipe with { SwapOnClick = swap, MatchMasterResolution = match, AvoidTaskbar = !overlap, AlwaysOnTop = pinned };
                var slots = Resolve(layout);
                var expected = session?.SourceHandles ?? handles.ToArray();
                var previousFrames = games.Select(g => g.Bounds).ToArray();
                var previousSurfaces = session?.SurfaceHandles;
                var checks = new List<string>();
                string? error = null;
                bool unsupported = false;
                void Check(bool condition, string text)
                {
                    if (!condition) throw new InvalidOperationException(text);
                    checks.Add(text);
                }
                try
                {
                    lastSample = null;
                    // Foregrounding the current master before changing modes also
                    // makes Explorer-style overlap independent of the preceding case.
                    AcquireFixtureFocus(games[Array.IndexOf(handles, expected[master])]);
                    if (session is null)
                        session = GameViewSession.StartForTest(layout, slots, handles, Resolve, _ => false, () => [taskbar.Handle]);
                    else session.ApplyForTest(layout, slots, handles);
                    Check(session.IsActive && session.OccupiedCount == 3 && session.SourceHandles.SequenceEqual(expected),
                        "Applying the switch preserves all three displayed character assignments.");

                    void CheckPictures()
                    {
                        // Move the desktop cursor and its highlight off the sampled game pixels.
                        if (Control.MouseButtons != MouseButtons.None || GetCapture() != 0 || OwnerAt(parking) != ordinary.Handle ||
                            SendInput(1, [Motion(parking)], Marshal.SizeOf<MousePacket>()) != 1)
                            throw new InvalidOperationException("Could not park the pointer over the unused fixture window.");
                        PumpUntil(() => AtRequestedPixel(Cursor.Position, parking), "The fixture pointer did not reach its parking point.");
                        for (int i = 0; i < slots.Length; i++)
                        {
                            var game = games[Array.IndexOf(handles, expected[i])];
                            var slot = slots[i];
                            var rendering = match ? slots[master].Size : slot.Size;
                            Check(game.ClientSize == rendering,
                                $"Slot {i + 1} renders at {rendering.Width}x{rendering.Height} (observed {game.ClientSize.Width}x{game.ClientSize.Height}).");
                            Check(session.TryGetVisibleClientRect(game.Handle, out var visible) && visible == slot,
                                $"Slot {i + 1} displays the assigned game's full client at {slot}.");
                            var surface = session.SurfaceHandles[i];
                            // Use independent desktop samples for identity, all four
                            // edges, and a UI control near the bottom of the real client.
                            var marker = HudMarker(rendering);
                            Point Map(Point pixel) => new(slot.Left + pixel.X * slot.Width / rendering.Width,
                                slot.Top + pixel.Y * slot.Height / rendering.Height);
                            var inside = Map(Center(marker));
                            var outside = Map(new Point(marker.Left - 8, marker.Top + marker.Height / 2));
                            PumpUntil(() => ColorVisible(surface, game.BackColor, Center(slot)) && EdgesVisible(surface, slot)
                                && ColorVisible(surface, Color.White, inside) && ColorVisible(surface, game.BackColor, outside),
                                $"Slot {i + 1} must show its character, four filled edges, and the UI marker at its expected scale.");
                        }
                        checks.Add("Game identity, edge coverage and UI positioning verified from the desktop.");
                    }
                    CheckPictures();
                    var barPoint = Center(taskbar.Bounds);
                    PumpUntil(() => OwnerAt(barPoint) == (overlap ? session.SurfaceHandles[master] : taskbar.Handle),
                        "The bottom strip must belong to the game or the taskbar as configured.");
                    checks.Add("Taskbar coverage matches Overlap Taskbar.");

                    int selected = step % 2;
                    var selectedGame = games[Array.IndexOf(handles, expected[selected])];
                    foreach (var game in games) game.Input.Clear();
                    if (swap)
                    {
                        PreviewSelection(session, selected);
                        (expected[master], expected[selected]) = (expected[selected], expected[master]);
                        Check(session.SourceHandles.SequenceEqual(expected) && Native.GetForegroundWindow() == selectedGame.Handle &&
                            games.All(g => g.Input.Count == 0), "Selecting a preview exchanges only those two seats and consumes the selection.");
                        CheckPictures();
                        var hud = Center(HudMarker(selectedGame.ClientSize));
                        NativeGesture(selectedGame, hud);
                        Check(selectedGame.Input.Count == 2 && selectedGame.Input.All(m => AtRequestedPixel(m.Point, hud)) &&
                            games.Where(g => g != selectedGame).All(g => g.Input.Count == 0),
                            "Clicking the visible HUD marker in the promoted native game reaches that same control.");
                    }
                    else
                    {
                        var target = Center(HudMarker(selectedGame.ClientSize));
                        if (match)
                        {
                            var surface = session.SurfaceHandles[selected];
                            Check(surface != selectedGame.Handle,
                                "An unfocused unequal fixed seat displays the character at the reference resolution.");
                            var slot = slots[selected];
                            var visibleMarker = new Point(slot.Left + target.X * slot.Width / selectedGame.ClientSize.Width,
                                slot.Top + target.Y * slot.Height / selectedGame.ClientSize.Height);
                            target = ClickFixedPreview(session, selected, visibleMarker);
                            Check(session.SurfaceHandles[selected] == surface && Native.GetForegroundWindow() == selectedGame.Handle,
                                "The first click on the preview HUD marker focuses the game while preserving its visible surface.");
                            CheckPictures();
                        }
                        else NativeGesture(selectedGame, target);
                        PumpUntil(() => selectedGame.Input.Any(m => m.Message == LeftUp),
                            "A fixed seat must accept the click without a swap.");
                        Check(session.SourceHandles.SequenceEqual(expected) && selectedGame.Input.Count == 2 &&
                            selectedGame.Input.All(m => AtRequestedPixel(m.Point, target)) &&
                            games.Where(g => g != selectedGame).All(g => g.Input.Count == 0),
                            "The fixed slot delivers its click at the corresponding in-game coordinates without exchanging seats.");
                    }

                    // Focusing a regular external-to-the-layout app removes the
                    // temporary taskbar overlap pin, isolating the Always on Top bit.
                    AcquireFixtureFocus(ordinary);
                    PumpUntil(() => session.SurfaceHandles.All(h => GameViewStacking.IsTopmost(h) == pinned),
                        "Always on Top must determine every visible surface's band while a different app is active.");
                    Check(session.SourceHandles.SequenceEqual(expected), "Switching away keeps all seats assigned.");
                    if (!swap && match)
                    {
                        PumpUntil(() => session.SurfaceHandles[selected] != selectedGame.Handle && selectedGame.ClientSize == slots[master].Size,
                            "Leaving a focused fixed client keeps its reference-size preview.");
                        PumpUntil(() => OwnerAt(barPoint) == (pinned && overlap ? session.SurfaceHandles[master] : taskbar.Handle),
                            "With another app focused, the taskbar is exposed unless the overlapping game stays pinned.");
                        AcquireFixtureFocus(games[Array.IndexOf(handles, expected[master])]);
                        PumpUntil(() => OwnerAt(barPoint) == (overlap ? session.SurfaceHandles[master] : taskbar.Handle),
                            "Returning to the game must restore its configured taskbar coverage.");
                        CheckPictures();
                    }
                    checks.Add("Verified Always on Top independently of the temporary taskbar cover.");
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    unsupported = ex is FixedSlotResolutionException;
                    bool preserved = games.Select(g => g.Bounds).SequenceEqual(previousFrames) && (session is null ||
                        session.IsActive && session.SourceHandles.SequenceEqual(expected) && session.SurfaceHandles.SequenceEqual(previousSurfaces!));
                    if (unsupported && preserved) checks.Add("The incompatible selection left the current layout and native windows intact.");
                    else
                    {
                        if (unsupported) error += " The rejected selection disturbed the preceding layout.";
                        session?.Dispose();
                        session = null;
                    }
                    failures.Add($"swap={swap}, match={match}, overlap={overlap}, topmost={pinned}: {error}");
                }
                results.Add(new { swap, match, overlap, alwaysOnTop = pinned, passed = error is null, unsupported, checks, error });
            }
        }
        finally
        {
            session?.Dispose();
            if (AtRequestedPixel(Cursor.Position, parking) && Control.MouseButtons == MouseButtons.None)
                Cursor.Position = originalPointer;
        }
        bool restored = games.Select(g => g.Bounds).SequenceEqual(original) && handles.All(h => !GameViewStacking.IsTopmost(h));
        CollectorFormat.WriteNew(output, new { passed = failures.Count == 0 && restored, fixtureWindows = 3, restored,
            combinations = results, gameInputSent = false, processMemoryRead = false });
        if (failures.Count > 0 || !restored)
            throw new InvalidOperationException($"Screen switch matrix failed {failures.Count}/16 combinations; original windows restored: {restored}. " +
                string.Join(Environment.NewLine, failures));
    }
}
