namespace TeamBar;

internal static class SpellOverlayLayoutTests
{
    internal static void Run(Action<bool, string> check)
    {
        var (definition, _) = SpellOverlayTests.Fixture();
        var legacy = System.Text.Json.JsonSerializer.Deserialize<SpellOverlayDefinition>(
            "{\"id\":\"legacy\",\"name\":\"Legacy\",\"character\":\"Example\",\"spells\":[]}", SpellOverlayStore.Json)!;
        check(legacy.Locked && legacy.Scale == 1, "existing overlays default to locked at their original size");
        var frame = new GameFrame("A", "First monitor", new(-1920, 0, 1920, 1080));
        var before = definition with { Screens = [new("B", "Other monitor", new(.2, .3))] };
        var moved = new SpellOverlayLayoutChange(Scale: 1.25, Locked: false, Frame: frame, Position: new(.4, .6)).Apply(before);
        check(moved is { X: .4, Y: .6, Scale: 1.25, Locked: false } && moved.Screens!.Length == 2,
            "layout gesture updates fallback and current-monitor placement while preserving other monitors");
        check(moved.Spells.SequenceEqual(before.Spells) && moved.Character == before.Character, "layout gesture preserves spell configuration and character");
        var again = new SpellOverlayLayoutChange(Frame: frame, Position: new(.5, .7)).Apply(moved);
        check(again.Screens!.Count(s => s.ScreenId == "A") == 1 && again.Screens!.Single(s => s.ScreenId == "B").Position.X == .2,
            "repeated moves replace only the active monitor's placement");
        var json = System.Text.Json.JsonSerializer.Serialize(moved, SpellOverlayStore.Json);
        var restored = System.Text.Json.JsonSerializer.Deserialize<SpellOverlayDefinition>(json, SpellOverlayStore.Json)!;
        check(restored.Scale == 1.25 && !restored.Locked, "size and lock persist across reload");
        foreach (double scale in new[] { .49, 2.01, double.NaN, double.PositiveInfinity })
        {
            bool rejected = false; try { new SpellOverlayLayoutChange(Scale: scale).Apply(definition); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "invalid scale rejected: " + scale);
        }
        var size = SpellOverlayLayout.Size(390, 305, 1.5, 1.25, frame);
        check(size.Width == 732 && size.Height == 572, "uniform resize accounts for DPI without an extra title bar");
        var small = new GameFrame("small", "Small window", new(100, 100, 300, 220));
        var fitted = SpellOverlayLayout.Size(390, 305, 1.5, 2, small);
        check(fitted.Width <= 300 && fitted.Height <= 220 && fitted.Height > 0, "spell area fits inside a small game window");
        var topLeft = SpellOverlayLayout.Position(new(-9999, -9999), size, frame);
        var bottomRight = SpellOverlayLayout.Position(new(9999, 9999), size, frame);
        check(topLeft == new RelativePosition(0, 0) && bottomRight == new RelativePosition(1, 1), "dragging clamps at screen edges including negative coordinates");
    }
}
