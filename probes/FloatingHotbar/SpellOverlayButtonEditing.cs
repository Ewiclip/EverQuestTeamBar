namespace TeamBar;

internal enum OverlayButtonAction { Link, Create, EditSynced, Remove }

internal sealed record OverlayButtonChange(OverlayButtonAction Action, SpellDockSide Side, string? GroupId = null,
    SavedButton? Source = null, StandaloneButton? Draft = null, int Column = 0,
    string? PlacementId = null, bool NewColumn = false, int? Row = null);

internal static class SpellOverlayButtonEditing
{
    internal static (SpellOverlayLibrary Library, byte[]? Bytes, string ButtonId) Save(string profile, string overlayPath,
        SpellOverlayLibrary library, byte[]? expectedOverlayBytes, string overlayId, OverlayButtonChange change)
    {
        var overlay = library.Overlays.SingleOrDefault(o => o.Id == overlayId)
            ?? throw new InvalidDataException("This spell bar no longer exists.");
        bool needsPlacement = change.Action is OverlayButtonAction.EditSynced or OverlayButtonAction.Remove;
        if (!Enum.IsDefined(change.Action) || !Enum.IsDefined(change.Side))
            throw new InvalidDataException("Choose a button action and an edge.");
        if (change.Action is not (OverlayButtonAction.Create or OverlayButtonAction.Remove) && change.Source is null)
            throw new InvalidDataException("Choose a saved button.");
        if (change.Action is OverlayButtonAction.Create or OverlayButtonAction.EditSynced && change.Draft is null)
            throw new InvalidDataException("Configure the button shortcuts.");
        var sourceId = change.Source?.Value.Id ?? change.PlacementId;
        string buttonId = change.Action == OverlayButtonAction.Create ? "button-" + Guid.NewGuid().ToString("N")
            : sourceId ?? throw new InvalidDataException("Choose the button to change.");
        var group = FindGroup(overlay, change, needsPlacement).CompactColumns();
        if (change.NewColumn && (needsPlacement || change.GroupId is null || change.Column != group.Columns))
            throw new InvalidDataException("The column layout has changed. Reopen the picker to add a column.");
        if (change.NewColumn && group.Columns >= ButtonColumns.MaxColumns)
            throw new InvalidDataException($"A group supports up to {ButtonColumns.MaxColumns} columns.");
        if (change.Row.HasValue && needsPlacement)
            throw new InvalidDataException("Choose a button slot when adding a button.");
        if (needsPlacement && (sourceId is null || !group.ButtonIds.Contains(sourceId)))
            throw new InvalidDataException("This button is no longer in the selected group. Reopen the editor.");
        StandaloneButton? edited = change.Action switch
        {
            OverlayButtonAction.Create => change.Draft! with { Id = buttonId, Floating = false },
            OverlayButtonAction.EditSynced => Edit(change.Source!.Value, change.Draft!),
            _ => null
        };
        if (edited is not null) StandaloneButtons.Validate(edited);
        var next = UpdateLayout(library, overlay, group, change, buttonId);
        SpellOverlayStore.Validate(next);

        return SpellOverlayStore.WithEditLock<(SpellOverlayLibrary, byte[]?, string)>(overlayPath, expectedOverlayBytes, () =>
        {
            string destination = StandaloneButtons.PathFor(profile, buttonId);
            if (change.Action == OverlayButtonAction.EditSynced)
            {
                StandaloneButtons.Save(destination, edited!, change.Source!.Bytes);
                return (library, expectedOverlayBytes, buttonId);
            }
            if (change.Action != OverlayButtonAction.Create)
            {
                // Link the revision shown in the picker. A missing button can still be removed.
                using var sourceGuard = change.Action == OverlayButtonAction.Link ? LockSource(profile, change.Source!) : null;
                return (next, SpellOverlayStore.WriteLocked(overlayPath, next), buttonId);
            }
            byte[] created = StandaloneButtons.Save(destination, edited!, null);
            try { return (next, SpellOverlayStore.WriteLocked(overlayPath, next), buttonId); }
            catch
            {
                // Roll back the new definition if the layout could not be saved.
                StandaloneButtons.Remove(destination, created);
                throw;
            }
        });
    }

    private static FileStream LockSource(string profile, SavedButton source)
    {
        string path = StandaloneButtons.PathFor(profile, source.Value.Id);
        var guard = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            StandaloneButtons.CheckUnchanged(path, source.Bytes);
            return guard;
        }
        catch { guard.Dispose(); throw; }
    }

    // Keep visibility, standalone floating behavior, follow settings, and extension
    // data owned by the original document when changing only its visible fields.
    private static StandaloneButton Edit(StandaloneButton saved, StandaloneButton draft) => saved with
    {
        Label = draft.Label, Icon = draft.Icon, Rules = draft.Rules,
        Characters = draft.Characters, Personas = draft.Personas
    };

    private static SpellDockGroup FindGroup(SpellOverlayDefinition overlay, OverlayButtonChange change, bool existing)
    {
        var groups = overlay.DockGroups ?? [];
        if (change.GroupId is { } id)
            return groups.FirstOrDefault(g => g.Id == id && g.Side == change.Side)
                ?? throw new InvalidDataException("The selected group moved or was removed. Reopen the editor.");
        if (existing) throw new InvalidDataException("Choose the group containing this button.");
        return groups.FirstOrDefault(g => g.Side == change.Side)
            ?? new SpellDockGroup(Guid.NewGuid().ToString("N"), change.Side + " buttons", change.Side, []);
    }

    private static SpellOverlayLibrary UpdateLayout(SpellOverlayLibrary library, SpellOverlayDefinition overlay, SpellDockGroup group,
        OverlayButtonChange change, string buttonId)
    {
        if (change.Action == OverlayButtonAction.EditSynced) return library;
        ButtonPlacement[] entries;
        if (change.Action == OverlayButtonAction.Remove)
            entries = ButtonColumns.Remove(group.Layout, buttonId, keepRows: group.Columns > 1);
        else
        {
            if (group.ButtonIds.Contains(buttonId)) throw new InvalidDataException("This saved button is already in this group.");
            if (!change.NewColumn && (change.Column < 0 || change.Column >= group.Columns))
                throw new InvalidDataException("This column is no longer available.");
            if (change.Row is int row) group.RequireVacantRow(change.Column, row);
            entries = ButtonColumns.Insert(group.Layout, new(buttonId, change.Column, change.Row), null);
        }
        // Materialize legacy rows with their original column count before widening the group.
        var replacement = group.WithLayout(entries);
        if (change.NewColumn)
            replacement = replacement with { Columns = group.Columns + 1, ColumnNames = ButtonColumns.Names(group.ColumnNames, group.Columns + 1) };
        else if (change.Action == OverlayButtonAction.Remove)
            replacement = replacement.CompactColumns();
        var groups = overlay.DockGroups ?? [];
        var updated = overlay with
        {
            DockGroups = groups.Any(g => g.Id == group.Id)
                ? groups.Select(g => g.Id == group.Id ? replacement : g).ToArray() : [.. groups, replacement]
        };
        // The manager migrates legacy per-spell personas before opening a bar.
        // A first dock edit upgrades its saved class-bar layout to the dock schema.
        return library with { Version = 3, Overlays = library.Overlays.Select(o => o.Id == overlay.Id ? updated : o).ToArray() };
    }
}
