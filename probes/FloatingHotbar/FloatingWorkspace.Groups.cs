using TeamBar;

internal sealed partial class FloatingWorkspace
{
    private bool RefreshGroup(Surface surface, DetectedClient[] identities, SavedButton[]? buttons = null)
    {
        var group = surface.Group!;
        var entries = group.Buttons.Select(member => (Member: member, Saved: (buttons ?? appliedButtons).SingleOrDefault(b => b.Value.Id == member.ButtonId)))
            .Where(entry => entry.Saved is null || StandaloneButtons.Visible(entry.Saved.Value, identities)).ToArray();
        string key = string.Join("|", entries.Select(e => e.Member.ButtonId)) + ":" +
            string.Join("|", identities.Select(c => $"{c.ProcessId}:{c.Character.Name}:{c.Character.ClassId}"));
        if (surface.MembersKey == key) return false;
        var taps = entries.Select(e => e.Saved is null ? [] : StandaloneButtons.Resolve(e.Saved.Value, identities)).ToArray();
        var actions = entries.Select(e => e.Saved is null
            ? new ButtonMapping(e.Member.ButtonId, "Missing button", [new("No matching recipients", "—")])
            : StandaloneButtons.Display(e.Saved.Value, identities)).ToArray();
        surface.Members = entries.Select(e => e.Member).ToArray(); surface.Actions = actions; surface.Taps = taps; surface.MembersKey = key;
        hovered = null; hoveredAction = -1; ClearActionTooltip();
        return true;
    }
    private RectangleF[] GroupCells(Surface surface)
    {
        var rows = new int[surface.Group!.Columns];
        float height = FloatingPainter.ActionHeight;
        float top = FloatingPainter.ActionBounds(0, FloatingPainter.ShowsHeader(surface.Heading, appearance)).Top;
        return surface.Members.Select(m => new RectangleF(m.Column * 224 + 9,
            top + rows[m.Column]++ * (height + 9), 206, height)).ToArray();
    }
    private Size GroupCanvas(Surface surface)
    {
        var cells = GroupCells(surface);
        int emptyHeight = FloatingPainter.ShowsHeader(surface.Heading, appearance) ? 55 : 40;
        return new(surface.Group!.Columns * 224, (int)Math.Ceiling(cells.Length == 0 ? emptyHeight : cells.Max(c => c.Bottom) + 9));
    }
}
