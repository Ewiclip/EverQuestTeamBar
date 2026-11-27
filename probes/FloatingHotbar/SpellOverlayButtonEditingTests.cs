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
        Change(first.Id, new(OverlayButtonAction.Link, SpellDockSide.Left, leftGroup.Id, source, Column: 1));
        Change(other.Id, new(OverlayButtonAction.Link, SpellDockSide.Bottom, Source: source));
        var otherGroup = library.Overlays[1].DockGroups!.Single();
        check(library.Version == 3 && library.Overlays[0].DockGroups![0].Layout.Single() == new ButtonPlacement(original.Id, 1)
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
        Reject<IOException>(() => Change(first.Id, new(OverlayButtonAction.MakeIndependent, SpellDockSide.Left, leftGroup.Id,
            source, renamed, ExistingPlacement: true)), "Making an independent copy rejects a concurrently edited source definition.");

        string detached = Change(first.Id, new(OverlayButtonAction.MakeIndependent, SpellDockSide.Left, leftGroup.Id,
            shared, shared.Value with { Icon = "melody" }, ExistingPlacement: true));
        var copied = Read(detached);
        var placed = SpellOverlayStore.Load(overlayPath).Library;
        var placedGroup = placed.Overlays[0].DockGroups!.Single();
        check(detached != original.Id && placedGroup.Layout.Single() == new ButtonPlacement(detached, 1)
            && placedGroup.Scale == leftGroup.Scale && placedGroup.ColumnNames!.SequenceEqual(leftGroup.ColumnNames!)
            && placed.Overlays[1].DockGroups!.Single().ButtonIds.Single() == original.Id && File.ReadAllBytes(originalPath).SequenceEqual(shared.Bytes)
            && copied.Value.Label == "" && copied.Value.Icon == "melody" && !copied.Value.Floating && copied.Value.Extra!["futureSetting"].GetString() == "retained",
            "Detaching replaces just the selected placement on disk, retaining the group's layout and the shared source revision.");
        Change(first.Id, new(OverlayButtonAction.EditSynced, SpellDockSide.Left, leftGroup.Id, copied,
            copied.Value with { Rules = [new(new(CharacterId: "cedar"), "F2")] }));
        check(Read(original.Id).Value.Rules.Single().Key == "Ctrl+Shift+Alt+Y" && Read(detached).Value.Rules.Single().Key == "F2",
            "After detaching, changing a local recipient shortcut does not change any linked instance.");
        Change(first.Id, new(OverlayButtonAction.Remove, SpellDockSide.Left, leftGroup.Id, Read(detached)));
        check(StandaloneButtons.Load(profile).Length == 2 && library.Overlays[0].DockGroups![0].Layout.Length == 0,
            "Removing a placement preserves its saved definition and the group's columns.");
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
        string unlinked = Change(other.Id, new(OverlayButtonAction.MakeIndependent, SpellDockSide.Right, Source: added));
        check(unlinked != added.Value.Id && library.Overlays[1].DockGroups!.Single(g => g.Side == SpellDockSide.Right).ButtonIds.Single() == unlinked
            && Read(unlinked).Value.Icon == added.Value.Icon, "The picker can place an independent copy directly.");
        for (int i = StandaloneButtons.Load(profile).Length; i < 20; i++)
        {
            var filler = original with { Id = "fixture-" + i };
            StandaloneButtons.Save(StandaloneButtons.PathFor(profile, filler.Id), filler, null);
        }
        Change(other.Id, new(OverlayButtonAction.Link, SpellDockSide.Top, Source: added));
        string next = Change(other.Id, create);
        string copy = Change(other.Id, new(OverlayButtonAction.MakeIndependent, SpellDockSide.Top, Source: added));
        check(StandaloneButtons.Load(profile).Length == 22 && Read(next).Value.Id != Read(copy).Value.Id,
            "Creating and copying buttons beyond twenty preserves the complete library without an account.");
        TestColumnPlacements(check, profile, Path.Combine(root, "columns.json"), added, [detached, unlinked, "legacy-action"]);
    }

    private static void TestColumnPlacements(Action<bool, string> check, string profile, string path, SavedButton source, string[] legacyIds)
    {
        var group = new SpellDockGroup("actions", "Actions", SpellDockSide.Top, legacyIds, Columns: 2)
            { ColumnNames = ["Travel", "Combat"] };
        var bar = new SpellOverlayDefinition("bar", "Fixture bar", "Cedar", [], PersonaClass: "Bard", DockGroups: [group]);
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
        var grown = SpellOverlayStore.Load(path).Library.Overlays.Single().DockGroups!.Single();
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
        string independent = Add(link with { Action = OverlayButtonAction.MakeIndependent, Column = 4 });
        grown = SpellOverlayStore.Load(path).Library.Overlays.Single().DockGroups!.Single();
        check(grown.Columns == 5 && grown.Layout.SequenceEqual([.. group.Layout, new(linked, 2), new(inExisting, 1),
                new(inNew, 3), new(independent, 4)]) && independent != linked,
            "Adding a row appends to the chosen column, and new or independent buttons each open one requested column.");
        for (int column = grown.Columns; column < ButtonColumns.MaxColumns; column++)
            Add(create with { Column = column, NewColumn = true });
        count = StandaloneButtons.Load(profile).Length;
        try { Add(create with { Column = ButtonColumns.MaxColumns, NewColumn = true }); throw new Exception("The column limit was exceeded."); }
        catch (InvalidDataException) { }
        check(File.ReadAllBytes(path).SequenceEqual(bytes!) && StandaloneButtons.Load(profile).Length == count,
            "A full group rejects another column before writing the layout or the new saved button.");
    }
}
