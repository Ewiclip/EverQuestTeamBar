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
        bool header = FloatingPainter.ShowsHeader(surface.Heading, appearance);
        return surface.Members.Select(m => FloatingPainter.ActionBounds(m.Column, header, rows[m.Column]++)).ToArray();
    }
    private Size GroupCanvas(Surface surface)
    {
        var cells = GroupCells(surface);
        bool header = FloatingPainter.ShowsHeader(surface.Heading, appearance);
        var size = FloatingPainter.LogicalSize(surface.Group!.Columns, header);
        size.Height = cells.Length == 0 ? (header ? 55 : 40) : (int)Math.Ceiling(cells.Max(c => c.Bottom)) + FloatingPainter.OuterPadding;
        return size;
    }
}
