using W = System.Windows;
namespace TeamBar;

internal sealed partial class SpellOverlayWindow
{
    internal static async Task TestSpellRows(Action<bool, string> check)
    {
        var (definition, capture, catalog) = SpellOverlayTests.GroupFixture();
        definition = definition with { Spells = definition.Spells.Select((s, i) => s with { ClickKey = i == 0 ? "Alt+6" : "Alt+7" }).ToArray() };
        var sent = new List<SpellClickRequest>();
        var display = new SpellOverlayWindow(FloatingThemes.Presets[1], (_, change) => definition = change.Apply(definition));
        try
        {
            var area = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
            var frame = new GameFrame("click-fixture", "Fixture", new(area.X + 10, area.Y + 10, 1000, 800));
            async Task Settle() => await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
            var state = SpellOverlayRules.Evaluate(definition, capture, catalog: catalog);
            display.Render(definition, state); display.Place(definition, frame); await Settle();
            display.SetClickContext(capture);
            Task<SpellClickReply?> Send(SpellClickRequest request) { sent.Add(request); return Task.FromResult<SpellClickReply?>(new(true, "Fixture tap", false)); }
            display.SetClickConnection(true, false, "Ready", Send);
            System.Drawing.Point Point(int index)
            {
                var row = display.clickRows[index].Border;
                var screen = row.PointToScreen(new(row.ActualWidth / 2, row.ActualHeight / 2));
                return new((int)screen.X, (int)screen.Y);
            }
            void Edge(int message, int index = 0) => display.RoutedSpellMouse(message, Point(index));
            nint foreground = Native.GetForegroundWindow();
            Edge(0x201); check(sent.Count == 0, "spell press alone sends no key");
            Edge(0x202); await Settle();
            check(sent.Count == 1 && sent[0].SpellId == 70420 && sent[0].Key == "Alt+6", "release over the parent sends its configured chord once");
            Edge(0x202); check(sent.Count == 1, "release without a new press cannot cast again");
            Edge(0x201, 1); Edge(0x202, 1); await Settle();
            check(sent.Count == 2 && sent[1].SpellId == 70420, "child effect click uses the parent cast key, not the triggered effect ID");
            Edge(0x201); display.RoutedSpellMouse(0x202, new(frame.Area.Left - 10, frame.Area.Top - 10));
            check(sent.Count == 2, "releasing outside the spell entry cancels casting");
            Edge(0x201); display.SetClickContext(capture with { Target = capture.Target! with { SpawnId = 99 } }); Edge(0x202);
            check(sent.Count == 2, "target change during a held click cancels casting");
            display.SetClickContext(capture);
            Edge(0x201); display.Place(definition, frame with { Area = new(frame.Area.X + 10, frame.Area.Y, 1000, 800) }); Edge(0x202);
            check(sent.Count == 2, "moving the game or overlay during a click cancels casting");
            display.ToggleLayoutLock(false); Edge(0x201); Edge(0x202);
            check(sent.Count == 2 && display.CanEdit, "unlocked overlay allows layout editing without casting");
            display.ToggleLayoutLock(true); display.SetClickConnection(true, true, "Paused", Send); Edge(0x201); Edge(0x202);
            check(sent.Count == 2, "paused spell entry sends no request");
            display.SetClickConnection(true, false, "Ready", Send); display.clickLeaseUntil = 0; Edge(0x201); Edge(0x202);
            check(sent.Count == 2, "expired controller lease cannot send a spell request");
            display.SetClickConnection(true, false, "Ready", Send);
            Edge(0x201); display.Render(definition, state with { Rows = state.Rows.Reverse().ToArray() }); Edge(0x202);
            check(sent.Count == 2, "reordering spell rows while pressed cancels the click");
            display.SetClickConnection(false, true, "Disconnected", Send);
            check(!display.IsHitTestVisible && display.clickShield is null, "disconnected spell overlay releases its mouse shield and becomes display-only");
            int rejected = 0;
            display.SetClickConnection(true, false, "Ready", _ =>
            {
                rejected++;
                return Task.FromResult<SpellClickReply?>(new(false, "Reconnecting", ReconnectRequired: true));
            });
            Edge(0x201); Edge(0x202); await Settle();
            check(rejected == 1 && !display.clickConnected && display.clickShield is null,
                "lost-registration reply revokes the window click lease and mouse shield");
            Edge(0x201); Edge(0x202);
            check(rejected == 1, "disconnected window waits for verified registration before another gesture");
            check(Native.GetForegroundWindow() == foreground, "spell clicks leave foreground unchanged");
        }
        finally { display.Close(); }
    }
}
