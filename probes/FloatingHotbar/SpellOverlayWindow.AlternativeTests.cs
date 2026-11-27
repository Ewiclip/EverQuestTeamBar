using W = System.Windows;
namespace TeamBar;

internal sealed partial class SpellOverlayWindow
{
    internal static async Task TestCasterAndAlternativeRows(Action<bool, string> check)
    {
        var (sample, capture) = SpellOverlayTests.Fixture();
        var rule = new SpellWatch(100, "Our protection", Caster: SpellCasterKind.Anyone, IconId: 56,
            ExpandTriggered: false, ClickKey: "Alt+H", Alternatives: [new(200, "Cleric protection", 104), new(300, "Druid protection", 131)]);
        var definition = sample with { Spells = [rule] };
        var display = new SpellOverlayWindow(FloatingThemes.Presets[1]);
        var sent = new List<SpellClickRequest>();
        var area = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        var frame = new GameFrame("alternatives-fixture", "Fixture", new(area.X + 10, area.Y + 10, 1000, 800));
        async Task Show(TargetEffect[] effects, SpellWatch? configured = null)
        {
            capture = capture with { Buffs = capture.Buffs! with { Effects = effects } };
            definition = definition with { Spells = [configured ?? rule] };
            display.Render(definition, SpellOverlayRules.Evaluate(definition, capture));
            display.Place(definition, frame);
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
        try
        {
            await Show([new(0, 200, "Cleric protection", "Friendlycleric", 90000, 104)]);
            var controls = display.rows[(1, 100, false)];
            check(display.body.Children.Count == 1 && display.clickRows.Single().Parent == rule,
                "approved alternatives render one row retaining the configured spell and click mapping");
            check(controls.Present.Text == "Present: Cleric protection" && controls.Present.IsVisible
                && controls.Detail.Text == "Alt+H · Cast by Friendlycleric" && controls.Time.Text == "1:30",
                "alternative row shows the observed spell, caster, configured key and observed countdown");
            check(controls.Border.ToolTip.ToString()!.Contains("Cast by Friendlycleric"),
                "full caster name remains available in the row tooltip and accessibility description");
            await Show([new(0, 300, "Druid protection", "Friendlydruid", 89000, 131)]);
            var updated = display.rows[(1, 100, false)];
            check(ReferenceEquals(controls.Border, updated.Border) && updated.Present.Text == "Present: Druid protection"
                && updated.Detail.Text == "Alt+H · Cast by Friendlydruid" && updated.Time.Text == "1:29",
                "observed alternative and caster refresh in place without rebuilding the configured row");
            await Show([new(0, 300, "Druid protection", null, 88000, 131)]);
            check(display.rows[(1, 100, false)].Detail.Text == "Alt+H · Caster unknown"
                && display.currentState!.Rows.Single().State == "Active",
                "anyone casts with unavailable names stay present without showing a stale caster");

            display.SetClickContext(capture);
            display.SetClickConnection(true, false, "Ready", request =>
            { sent.Add(request); return Task.FromResult<SpellClickReply?>(new(true, "Fixture tap", false)); });
            var border = display.clickRows.Single().Border;
            var point = border.PointToScreen(new(border.ActualWidth / 2, border.ActualHeight / 2));
            var screen = new System.Drawing.Point((int)point.X, (int)point.Y);
            display.RoutedSpellMouse(0x201, screen); display.RoutedSpellMouse(0x202, screen);
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
            check(sent.Count == 1 && sent[0].SpellId == 100 && sent[0].Key == "Alt+H",
                "clicking a row fulfilled by an alternative sends only the original configured spell key");

            await Show([]);
            var absent = display.rows[(1, 100, false)];
            check(display.body.Children.Count == 1 && absent.Time.Text == "Missing"
                && absent.Present.Visibility == W.Visibility.Collapsed && absent.Detail.Text == "Alt+H"
                && !absent.Border.ToolTip.ToString()!.Contains("Cast by"),
                "all alternatives absent show one missing indicator and clear the previous spell and caster");
            await Show([new(0, 100, "Our protection", capture.CharacterName, 87000, 56)], rule with { Caster = SpellCasterKind.Mine });
            check(display.rows[(1, 100, false)].Detail.Text == "Alt+H",
                "my casts keeps the compact key label without redundant caster attribution");
            await Show([new(0, 100, "Our protection", "Friendlycleric", 86000, 56)], rule with { Alternatives = null });
            check(display.rows[(1, 100, false)].Detail.Text == "Alt+H · Cast by Friendlycleric"
                && display.rows[(1, 100, false)].Present.Visibility == W.Visibility.Collapsed,
                "ordinary anyone-cast rows show the caster without a redundant spell detail");
            display.Render(definition, SpellOverlayRules.Evaluate(definition, null, "Reading unavailable"));
            check(display.body.Children.Count == 0 && display.rows.Count == 0,
                "unavailable target data clears alternative and caster details with the spell rows");
        }
        finally { display.Close(); }
    }
}
