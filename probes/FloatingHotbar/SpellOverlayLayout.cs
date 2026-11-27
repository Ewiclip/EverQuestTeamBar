namespace TeamBar;

internal sealed record SpellOverlayLayoutChange(bool? Locked = null, double? Scale = null,
    GameFrame? Frame = null, RelativePosition? Position = null, string? GroupId = null, double? GroupScale = null,
    OverlayDesktopPosition? FreePosition = null)
{
    internal SpellOverlayDefinition Apply(SpellOverlayDefinition definition, bool validate = true)
    {
        var next = definition with { Locked = Locked ?? definition.Locked, Scale = Scale ?? definition.Scale,
            FreePosition = FreePosition ?? definition.FreePosition };
        if (GroupScale is { } groupScale)
        {
            if (GroupId is null || definition.DockGroups?.Any(g => g.Id == GroupId) != true) throw new InvalidDataException("This button group changed. Reopen its settings.");
            next = next with { DockGroups = definition.DockGroups.Select(g => g.Id == GroupId ? g with { Scale = groupScale } : g).ToArray() };
        }
        if (Position is { } position)
        {
            if (Frame is null) throw new InvalidDataException("The character's screen is no longer available.");
            next = next with { X = position.X, Y = position.Y, Screens =
                [.. (definition.Screens ?? []).Where(s => !s.ScreenId.Equals(Frame.ScreenId, StringComparison.OrdinalIgnoreCase)),
                 new(Frame.ScreenId, Frame.ScreenLabel, position)] };
        }
        if (validate) SpellOverlayStore.Validate(new(1, [next])); return next;
    }
}

internal static class SpellOverlayLayout
{
    internal static GameFrame DesktopFrame(System.Drawing.Point point, System.Drawing.Rectangle[] areas, System.Drawing.Size? control = null)
    {
        var visible = WindowLayouts.Clamp(point, control ?? new(1, 1), areas);
        return new("desktop", "Desktop", areas.First(a => a.Contains(visible)));
    }
    internal static System.Drawing.Size NaturalPixels(double width, double height, double dpi, double scale)
        => new((int)Math.Ceiling(width * dpi * scale), (int)Math.Ceiling(height * dpi * scale));
    internal static System.Drawing.Size Size(double width, double height, double dpi, double scale, GameFrame frame)
    {
        int w = (int)Math.Ceiling(width * dpi * scale), h = (int)Math.Ceiling(height * dpi * scale);
        double fit = Math.Min(1, Math.Min((double)frame.Area.Width / Math.Max(1, w),
            (double)Math.Max(1, frame.Area.Height) / Math.Max(1, h)));
        return new(Math.Max(1, (int)(w * fit)), Math.Max(1, (int)(h * fit)));
    }
    internal static RelativePosition Position(System.Drawing.Point desired, System.Drawing.Size size, GameFrame frame)
        => AnchorPositions.Normalize(new(Math.Clamp(desired.X, frame.Area.Left, Math.Max(frame.Area.Left, frame.Area.Right - size.Width)),
            Math.Clamp(desired.Y, frame.Area.Top, Math.Max(frame.Area.Top, frame.Area.Bottom - size.Height))), size, frame.Area);
}
