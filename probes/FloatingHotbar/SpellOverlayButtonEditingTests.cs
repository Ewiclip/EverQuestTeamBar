using System.Text.Json;

namespace TeamBar;

internal static class SpellOverlayButtonEditingTests
{
    internal static void Run(Action<bool, string> check, string output)
    {
        string root = Path.GetFullPath(output) + ".overlay-edits";
        if (Directory.Exists(root)) throw new IOException("Button editor tests require a new fixture directory.");
        Directory.CreateDirectory(root);
        string profile = Path.Combine(root, "team.json"), overlayPath = Path.Combine(root, "overlays.json");
        var team = new TeamDefinition(1, [new("cedar", "Cedar")], [new("cedar-brd", "cedar", "Bard")], []);
        TeamEditing.Save(profile, team, []);
        var original = new StandaloneButton(1, "follow", "Follow", team.Characters, team.Personas,
            [new(new(CharacterId: "cedar"), "2")], [new(PersonaId: "cedar-brd")], "cedar")
        {
            Extra = new() { ["futureSetting"] = JsonSerializer.SerializeToElement("retained") }
        };
        string originalPath = StandaloneButtons.PathFor(profile, original.Id);
        StandaloneButtons.Save(originalPath, original, null);
        SavedButton Read(string id) => StandaloneButtons.Load(profile).Single(s => s.Value.Id == id);
        var leftGroup = new SpellDockGroup("left", "Actions", SpellDockSide.Left, [], Scale: 1.25, Columns: 2)
            { ColumnNames = ["Utility", "Combat"] };
        var first = new SpellOverlayDefinition("one", "First", "Cedar", [], PersonaClass: "Bard", DockGroups: [leftGroup]);
        var other = first with { Id = "two", Name = "Second", DockGroups = null };
        var library = new SpellOverlayLibrary(2, [first, other]);
        byte[]? bytes = SpellOverlayStore.Save(overlayPath, library, null);
        string Change(string overlay, OverlayButtonChange change)
        {
            var result = SpellOverlayButtonEditing.Save(profile, overlayPath, library, bytes, overlay, change);
            library = result.Library; bytes = result.Bytes;
            return result.ButtonId;
        }
        void Reject<T>(Action action, string reason) where T : Exception
        {
            try { action(); }
            catch (T) { check(true, reason); return; }
            check(false, reason);
        }
        var source = Read(original.Id);
        Change(first.Id, new(OverlayButtonAction.Link, SpellDockSide.Left, leftGroup.Id, source));
        Change(other.Id, new(OverlayButtonAction.Link, SpellDockSide.Bottom, Source: source));
        var otherGroup = library.Overlays[1].DockGroups!.Single();
        check(library.Version == 3 && library.Overlays[0].DockGroups![0] is { Columns: 1 } firstGroup
            && firstGroup.Layout.Single() == new ButtonPlacement(original.Id, 0)
            && otherGroup.ButtonIds.Single() == original.Id && StandaloneButtons.Load(profile).Length == 1,
            "A saved button can be linked on two bars, keeping its ID, column, and single definition.");
        Reject<InvalidDataException>(() => Change(first.Id, new(OverlayButtonAction.Link, SpellDockSide.Left, leftGroup.Id, source)),
            "Adding a duplicate saved placement leaves its existing position intact.");

        var renamed = source.Value with
        {
            Label = "", Icon = "swords", Rules = [new(new(CharacterId: "cedar"), "Ctrl+Shift+Alt+Y")],
            Extra = null, ShowFor = [], FollowCharacterId = null, Floating = false
        };
        var beforeSharedEdit = bytes!;
        Change(first.Id, new(OverlayButtonAction.EditSynced, SpellDockSide.Left, leftGroup.Id, source, renamed));
        var shared = Read(original.Id);
        check(beforeSharedEdit.SequenceEqual(File.ReadAllBytes(overlayPath)) && shared.Value.Label == ""
            && PickerButtonInfo.From(shared.Value, true).Name == "Attack · Ctrl+Shift+Alt+Y"
            && shared.Value.Icon == "swords" && shared.Value.Rules.Single().Key == "Ctrl+Shift+Alt+Y"
            && shared.Value.Floating && shared.Value.FollowCharacterId == "cedar" && shared.Value.ShowFor.Single().PersonaId == "cedar-brd"
            && shared.Value.Extra!["futureSetting"].GetString() == "retained",
            "Clearing a synced button name keeps its icon, shortcuts and both placements. The library describes its icon and shortcut.");
        Reject<IOException>(() => Change(other.Id, new(OverlayButtonAction.EditSynced, SpellDockSide.Bottom, otherGroup.Id,
            source, renamed)), "A second placement cannot overwrite a newer edit using a stale revision.");
        Reject<IOException>(() => Change(other.Id, new(OverlayButtonAction.Link, SpellDockSide.Right, Source: source)),
            "The picker rejects a saved button that changed after it opened.");

        // Older profiles can contain separate button IDs with the same label and icon.
        var legacy = shared.Value with { Id = "button-legacy", Floating = false };
        StandaloneButtons.Save(StandaloneButtons.PathFor(profile, legacy.Id), legacy, null);
        Change(first.Id, new(OverlayButtonAction.Link, SpellDockSide.Left, leftGroup.Id, Read(legacy.Id)));
        Change(first.Id, new(OverlayButtonAction.EditSynced, SpellDockSide.Left, leftGroup.Id, Read(legacy.Id),
            legacy with { Rules = [new(new(CharacterId: "cedar"), "F2")] }));
        check(Read(legacy.Id).Value.Rules.Single().Key == "F2" && File.ReadAllBytes(originalPath).SequenceEqual(shared.Bytes),
            "A pre-existing same-name button keeps its own ID and mappings when edited.");
        Change(first.Id, new(OverlayButtonAction.Remove, SpellDockSide.Left, leftGroup.Id, shared));
        var placed = SpellOverlayStore.Load(overlayPath).Library;
        var placedGroup = placed.Overlays[0].DockGroups!.Single();
        check(StandaloneButtons.Load(profile).Length == 2 && placedGroup.Layout.Single() == new ButtonPlacement(legacy.Id, 0)
            && placedGroup.Scale == leftGroup.Scale && placedGroup.ColumnNames!.SequenceEqual(["Utility"])
            && placed.Overlays[1].DockGroups!.Single().ButtonIds.Single() == original.Id,
            "Removing a placement preserves the shared definition, its other placement and its group's settings.");
        StandaloneButtons.Remove(originalPath, shared.Bytes);
        Change(other.Id, new(OverlayButtonAction.Remove, SpellDockSide.Bottom, otherGroup.Id, PlacementId: original.Id));
        check(library.Overlays[1].DockGroups!.Single().ButtonIds.Length == 0,
            "A placement with a missing saved definition can still be removed.");

        var create = new OverlayButtonChange(OverlayButtonAction.Create, SpellDockSide.Top, Draft: original with { Label = "", Icon = "follow" });
        Reject<IOException>(() => SpellOverlayButtonEditing.Save(profile, overlayPath, library, beforeSharedEdit, first.Id, create),
            "Outdated overlay revisions are rejected before allocating a button.");
        using (File.Open(overlayPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            bool replacementDenied = false;
            try { Change(first.Id, create); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { replacementDenied = true; }
            check(replacementDenied, "A layout replacement failure aborts the combined add operation.");
        }
        check(StandaloneButtons.Load(profile).Length == 1 && File.ReadAllBytes(overlayPath).SequenceEqual(bytes!),
            "A failed layout write leaves both the active slot count and the existing saved bar unchanged.");
        string created = Change(first.Id, create);
        var added = Read(created);
        check(added.Value.Label == "" && added.Value.Icon == "follow" && !added.Value.Floating && library.Overlays[0].DockGroups!.Single(g => g.Side == SpellDockSide.Top).ButtonIds.Single() == created,
            "Creating an unnamed button on an empty edge saves the icon and mappings and creates its first placement.");
        string linked = Change(other.Id, new(OverlayButtonAction.Link, SpellDockSide.Right, Source: added));
        check(linked == created && library.Overlays[1].DockGroups!.Single(g => g.Side == SpellDockSide.Right).ButtonIds.Single() == linked
            && StandaloneButtons.Load(profile).Length == 2, "A newly created button can be reused without allocating another definition.");
        for (int i = StandaloneButtons.Load(profile).Length; i < 20; i++)
        {
            var filler = original with { Id = "fixture-" + i };
            StandaloneButtons.Save(StandaloneButtons.PathFor(profile, filler.Id), filler, null);
        }
        Change(other.Id, new(OverlayButtonAction.Link, SpellDockSide.Top, Source: added));
        string next = Change(other.Id, create);
        check(StandaloneButtons.Load(profile).Length == 21 && Read(next).Value.Id != created,
            "Creating a button beyond twenty saves a separate definition without an account.");
        TestColumnPlacements(check, profile, Path.Combine(root, "columns.json"), added, [legacy.Id, next, "legacy-action"]);
    }

    private static void TestColumnPlacements(Action<bool, string> check, string profile, string path, SavedButton source, string[] legacyIds)
    {
        var group = new SpellDockGroup("actions", "Actions", SpellDockSide.Top, legacyIds, Columns: 2)
            { ColumnNames = ["Travel", "Combat"] };
        var sparse = new SpellDockGroup("sparse", "Sparse", SpellDockSide.Left, legacyIds, Scale: 1.2, Columns: 6,
            Placements: [new(legacyIds[0], 4), new(legacyIds[1], 1), new(legacyIds[2], 3)])
            { ColumnNames = ["Unused 1", "Travel", "Unused 3", "Combat", "Support", "Unused 6"] };
        var dense = new SpellDockGroup("dense", "Dense", SpellDockSide.Bottom,
            ["left-top", "right-top", "left-middle", source.Value.Id, "left-bottom", "right-bottom"], Columns: 2);
        var bar = new SpellOverlayDefinition("bar", "Fixture bar", "Cedar", [], PersonaClass: "Bard", DockGroups: [group, sparse, dense]);
        var library = new SpellOverlayLibrary(3, [bar]);
        byte[]? bytes = SpellOverlayStore.Save(path, library, null);
        string Add(OverlayButtonChange change)
        {
            var result = SpellOverlayButtonEditing.Save(profile, path, library, bytes, bar.Id, change);
            library = result.Library; bytes = result.Bytes;
            return result.ButtonId;
        }
        var link = new OverlayButtonChange(OverlayButtonAction.Link, group.Side, group.Id, source, Column: 2, NewColumn: true);
        var create = new OverlayButtonChange(OverlayButtonAction.Create, group.Side, group.Id, Draft: source.Value);
        string linked = Add(link);
        SpellDockGroup ReadGroup(string id) => SpellOverlayStore.Load(path).Library.Overlays.Single().DockGroups!.Single(g => g.Id == id);
        var grown = ReadGroup(group.Id);
        check(grown.Columns == 3 && grown.ColumnNames!.SequenceEqual(["Travel", "Combat", "Column 3"])
            && grown.Layout.SequenceEqual([.. group.Layout, new ButtonPlacement(linked, 2)]),
            "Adding a column and its first linked button preserves names and the order of legacy placements.");
        int count = StandaloneButtons.Load(profile).Length;
        try { Add(create with { Column = 2, NewColumn = true }); throw new Exception("An old add-column request was accepted twice."); }
        catch (InvalidDataException) { }
        check(File.ReadAllBytes(path).SequenceEqual(bytes!) && StandaloneButtons.Load(profile).Length == count,
            "Retrying an outdated add-column request allocates neither a column nor a saved button.");

        string inExisting = Add(create with { Column = 1 });
        string inNew = Add(create with { Column = 3, NewColumn = true });
        grown = ReadGroup(group.Id);
        check(grown.Columns == 4 && grown.Layout.SequenceEqual([.. group.Layout, new(linked, 2), new(inExisting, 1),
                new(inNew, 3)]) && inExisting != inNew && inNew != linked,
            "Creating a button in an existing row or new column gives it a distinct ID in the requested location.");
        for (int column = grown.Columns; column < ButtonColumns.MaxColumns; column++)
            Add(create with { Column = column, NewColumn = true });
        count = StandaloneButtons.Load(profile).Length;
        try { Add(create with { Column = ButtonColumns.MaxColumns, NewColumn = true }); throw new Exception("The column limit was exceeded."); }
        catch (InvalidDataException) { }
        check(File.ReadAllBytes(path).SequenceEqual(bytes!) && StandaloneButtons.Load(profile).Length == count,
            "A full group rejects another column before writing the layout or the new saved button.");
        try { Add(create with { Column = 0, Row = 1 }); throw new Exception("An occupied cell was offered as a vacant row."); }
        catch (InvalidDataException) { }
        check(File.ReadAllBytes(path).SequenceEqual(bytes!) && StandaloneButtons.Load(profile).Length == count,
            "Opening a stale picker for an occupied row cannot displace its button or allocate an unused definition.");
        Add(new(OverlayButtonAction.Link, sparse.Side, sparse.Id, source, Column: 3, NewColumn: true));
        var compact = ReadGroup(sparse.Id);
        check(compact.Columns == 4 && compact.ColumnNames!.SequenceEqual(["Travel", "Combat", "Support", "Column 4"])
            && compact.Layout.SequenceEqual([new(legacyIds[0], 2), new(legacyIds[1], 0), new(legacyIds[2], 1), new(source.Value.Id, 3)]),
            "Adding a column to an older sparse dock places it immediately after occupied columns and retains their names.");
        Add(new(OverlayButtonAction.Remove, sparse.Side, sparse.Id, PlacementId: legacyIds[2]));
        compact = ReadGroup(sparse.Id);
        check(compact.Columns == 3 && compact.Scale == sparse.Scale && compact.ColumnNames!.Take(2).SequenceEqual(["Travel", "Support"])
            && compact.PositionedLayout.ToHashSet().SetEquals([new(legacyIds[0], 1, 0), new(legacyIds[1], 0, 0), new(source.Value.Id, 2, 0)])
            && StandaloneButtons.Load(profile).Length == count && ReadGroup(group.Id).Columns == ButtonColumns.MaxColumns,
            "Removing the last button from a middle column closes the gap on disk and keeps the other group and saved buttons intact.");
        Add(new(OverlayButtonAction.Remove, dense.Side, dense.Id, source));
        var withGap = ReadGroup(dense.Id);
        check(withGap.PositionedLayout.ToHashSet().SetEquals(dense.PositionedLayout.Where(p => p.ButtonId != source.Value.Id)) &&
            withGap.PositionedLayout.Contains(new ButtonPlacement("right-bottom", 1, 2)),
            "Removing the middle button of a full two-column dock keeps its third row aligned across both columns.");
        Add(new(OverlayButtonAction.Link, dense.Side, dense.Id, source, Column: 1, Row: 1));
        check(ReadGroup(dense.Id).PositionedLayout.ToHashSet().SetEquals(dense.PositionedLayout),
            "The same saved button can fill the vacated middle cell and restore every original coordinate.");
    }
}
