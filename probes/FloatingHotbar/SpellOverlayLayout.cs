namespace TeamBar;

internal sealed record SpellOverlayLayoutChange(bool? Locked = null, double? Scale = null,
    GameFrame? Frame = null, RelativePosition? Position = null, string? GroupId = null, double? GroupScale = null,
    OverlayDesktopPosition? FreePosition = null, string? GroupName = null, string? ExpectedGroupName = null, DockButtonMove? ButtonMove = null)
{
    internal SpellOverlayDefinition Apply(SpellOverlayDefinition definition, bool validate = true)
    {
        var next = ButtonMove?.Apply(definition) ?? definition;
        if (Locked is not null || Scale is not null || FreePosition is not null)
            next = next with { Locked = Locked ?? next.Locked, Scale = Scale ?? next.Scale, FreePosition = FreePosition ?? next.FreePosition };
        if (GroupScale is not null || GroupName is not null)
        {
            var groups = next.DockGroups ?? [];
            var group = groups.SingleOrDefault(g => g.Id == GroupId)
                ?? throw new InvalidDataException("This button group changed. Reopen its settings.");
            var updated = group with { Scale = GroupScale ?? group.Scale };
            if (GroupName is { } name)
            {
                if (group.Name != ExpectedGroupName || group.ButtonIds.Length == 0)
                    throw new InvalidDataException("This button group changed. Reopen its title to rename it.");
                updated = updated with { Name = string.IsNullOrWhiteSpace(name) ? "" : name };
            }
            next = next with { DockGroups = groups.Select(g => g.Id == GroupId ? updated : g).ToArray() };
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
