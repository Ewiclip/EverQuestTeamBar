using System.Text.Json;
namespace TeamBar;

internal static class SpellDockTests
{
    internal static SavedButton[] Buttons()
    {
        CharacterProfile[] characters = [new("fellwort", "Fellwort"), new("runa", "Runa")];
        PersonaProfile[] personas = [new("shaman", "fellwort", "Shaman"), new("sk", "runa", "Shadowknight")];
        return new[] { ("invis", "All invis", "5"), ("assist", "Assist", "1"), ("heal", "Quick heal", "2"), ("cure", "Cure", "3") }.Select(entry =>
        {
            var value = new StandaloneButton(1, entry.Item1, entry.Item2, characters, personas,
                [new(new(CharacterId: "fellwort"), entry.Item3), new(new(CharacterId: "runa"), entry.Item3)], []);
            return new SavedButton("fixture-" + value.Id, value, JsonSerializer.SerializeToUtf8Bytes(value, Program.Json));
        }).ToArray();
    }
    internal static DetectedClient[] Clients() => [new(101, 1001, 11, 0, new("Fellwort", 10, 130)), new(202, 2002, 22, 0, new("Runa", 5, 130))];
    internal static SavedButton[] WithConflict(SavedButton[] buttons) => buttons.Select(saved =>
    {
        if (saved.Value.Id != "invis") return saved;
        var value = saved.Value with { Rules = [.. saved.Value.Rules, new(new(PersonaId: "shaman"), "3"), new(new(CharacterId: "fellwort", Class: "Shaman"), "4")] };
        return saved with { Value = value, Bytes = JsonSerializer.SerializeToUtf8Bytes(value, Program.Json) };
    }).ToArray();
    internal static SpellDockGroup[] Groups() => [new("support", "Support", SpellDockSide.Bottom, ["invis", "assist"], .9, 2), new("emergency", "Quick actions", SpellDockSide.Right, ["heal", "cure"], .8)];
    internal static void Run(Action<bool, string> check, string output)
    {
        var namedGroup = Groups()[0] with { ColumnNames = ["Utility", "Combat"] };
        var restoredNames = JsonSerializer.Deserialize<SpellDockGroup>(JsonSerializer.Serialize(namedGroup, SpellOverlayStore.Json), SpellOverlayStore.Json)!;
        check(restoredNames.ColumnNames!.SequenceEqual(namedGroup.ColumnNames) && restoredNames.Scale == namedGroup.Scale
            && restoredNames.Layout.SequenceEqual(namedGroup.Layout), "Docked column labels round-trip without changing existing placement or size.");
        var (definition, capture, catalog) = SpellOverlayTests.GroupFixture();
        definition = definition with { Character = "Fellwort", PersonaClass = "Shaman", DockGroups = Groups(),
            Spells = definition.Spells.Select(s => s with { PersonaClasses = null, Alias = s.SpellId == 70420 ? "Bloodcurse" : null }).ToArray() };
        var clients = Clients(); var buttons = Buttons();
        capture = capture with { ProcessId = 101, ProcessStartedUtcTicks = 1001, CharacterName = "Fellwort",
            Buffs = capture.Buffs! with { ProcessId = 101, ProcessStartedUtcTicks = 1001, CharacterName = "Fellwort", Effects = [] } };
        var state = SpellOverlayRules.Evaluate(definition, capture, catalog: catalog);
        check(state.Rows[0].Name == "Bloodcurse" && state.Rows[0].Rule.Name == "Chaotic Bloodcurse Rk. III"
            && state.Rows[0].Children!.Select(c => c.Name).SequenceEqual(["Caustic Blood", "Nectar of Obscurity"]),
            "alias replaces only the parent display label and retains original spell identity and triggered child names");
        check((definition.Spells[0] with { Alias = "  " }).DisplayName == definition.Spells[0].Name, "empty aliases fall back to the installed spell name");
        var saved = SpellOverlayStore.Save(output + ".docks.json", new(3, [definition]), null);
        var loaded = SpellOverlayStore.Load(output + ".docks.json").Library;
        check(loaded.Version == 3 && loaded.Overlays[0].DockGroups![0].ButtonIds.SequenceEqual(["invis", "assist"])
            && loaded.Overlays[0].Spells[0].Alias == "Bloodcurse", "aliases and ordered docked groups persist in a version-three profile");
        check(SpellOverlayMigration.Upgrade(loaded) == loaded, "class migration leaves the new profile version intact");
        var unnamed = definition with { Name = "", DockGroups = [namedGroup with { Name = "" }] };
        SpellOverlayStore.Save(output + ".unnamed-dock.json", new(3, [unnamed]), null);
        var restored = SpellOverlayStore.Load(output + ".unnamed-dock.json").Library.Overlays.Single();
        check(restored.Id == unnamed.Id && restored.Name == "" && restored.DockGroups!.Single() is { Name: "", Id: "support" } restoredGroup
            && restoredGroup.Layout.SequenceEqual(namedGroup.Layout), "Unnamed spell bars and docked groups reopen with the same IDs and layout.");
        void Invalid(SpellOverlayDefinition invalid, string name) { bool rejected = false; try { SpellOverlayStore.Validate(new(3, [invalid])); } catch (InvalidDataException) { rejected = true; } check(rejected, name); }
        Invalid(definition with { Spells = [definition.Spells[0] with { Alias = "Bad\nAlias" }] }, "multiline spell aliases are rejected");
        Invalid(definition with { Spells = [definition.Spells[0] with { Alias = new string('a', 81) }] }, "overlong aliases are rejected");
        foreach (var group in new[] { Groups()[0] with { Scale = double.NaN }, Groups()[0] with { Scale = 3 }, Groups()[0] with { Columns = 0 }, Groups()[0] with { Side = (SpellDockSide)99 }, Groups()[0] with { ButtonIds = ["invis", "invis"] }, Groups()[0] with { ButtonIds = ["../bad"] } })
            Invalid(definition with { DockGroups = [group] }, "invalid dock geometry or membership is rejected: " + group);
        var resize = new SpellOverlayLayoutChange(GroupId: "support", GroupScale: 1.4).Apply(definition);
        check(resize.Scale == definition.Scale && resize.DockGroups![0].Scale == 1.4 && resize.DockGroups[1] == definition.DockGroups![1]
            && resize.Spells.SequenceEqual(definition.Spells), "resizing one group preserves the spell body, other groups and mappings");
        var entry = SpellDockRules.Describe("invis", buttons, clients);
        var request = new SpellClickRequest("dock-tap", OverlayId: definition.Id, ProcessId: 101, Started: 1001, Class: "Shaman", ZoneId: capture.Zone.Id,
            TargetId: capture.Target!.SpawnId, TargetName: capture.Target.Name, GroupId: "support", ButtonId: "invis", ButtonRevision: entry.Revision, Recipients: entry.Recipients);
        RoutedTap[] Resolve(SpellClickRequest r, SpellOverlayDefinition? d = null, TargetCapture? c = null, SavedButton[]? b = null) => SpellDockRules.Resolve(new(3, [d ?? definition]), b ?? buttons, r, clients, (_, _) => c ?? capture);
        check(Resolve(request).Select(t => t.ProcessId).SequenceEqual([101, 202]), "one docked button routes its saved mapping to both configured characters");
        check(Resolve(request with { TargetId = 0, TargetName = null }, c: capture with { Target = null }).Length == 2,
            "docked utility buttons work with no selected target");
        void Reject(SpellClickRequest r, string name, SpellOverlayDefinition? d = null, TargetCapture? c = null, SavedButton[]? b = null)
        { bool rejected = false; try { Resolve(r, d, c, b); } catch (InvalidDataException) { rejected = true; } check(rejected, name); }
        void RejectRoster(SpellClickRequest r, string name)
        { bool rejected = false; try { Resolve(r); } catch (InputRosterChangedException) { rejected = true; } check(rejected, name); }
        Reject(request with { GroupId = "absent" }, "a button cannot dispatch outside its saved group");
        Reject(request with { ButtonRevision = "old" }, "edited standalone mappings reject an older captured click");
        RejectRoster(request with { Recipients = "old" }, "changed recipient sets request character refresh without silently broadcasting");
        RejectRoster(request with { Class = "Berserker" }, "a stale owner persona requests character refresh");
        RejectRoster(request with { Started = request.Started + 1 }, "a stale owner process session requests character refresh");
        RejectRoster(request with { ProcessId = request.ProcessId + 1 }, "a stale owner PID requests character refresh");
        Reject(request, "unlocked dock groups cannot dispatch", definition with { Locked = false });
        Reject(request, "dock clicks reject a target changed during the gesture", c: capture with { Target = null });
        Reject(request, "removed standalone button references fail closed", b: []);
        check(!SpellDockRules.Describe("missing", buttons, clients).Available, "missing standalone buttons display an unavailable placeholder");
        check(SpellDockRules.RecipientsFor([definition], buttons, clients).Length == 2, "spell-only input host discovers recipients from referenced group buttons");
        var conflicted = WithConflict(buttons);
        var conflictEntry = SpellDockRules.Describe("invis", conflicted, clients);
        check(conflictEntry.Visible && conflictEntry.HasConflict && !conflictEntry.Available && conflictEntry.Hint.Contains("Conflicting selectors")
            && SpellDockRules.Describe("assist", conflicted, clients).Available,
            "a conflicted docked button remains visible with its reason while unrelated buttons remain available");
        bool conflictRejected = false;
        try { Resolve(request with { ButtonRevision = conflictEntry.Revision, Recipients = "" }, b: conflicted); }
        catch (InvalidDataException error) { conflictRejected = error.Message.Contains("Conflicting selectors"); }
        check(conflictRejected, "a forged current-revision dock click cannot send a partial fan-out from a conflicted button");
        check(SpellDockRules.RecipientsFor([definition with { DockGroups = [Groups()[0] with { ButtonIds = ["invis"] }] }], conflicted, clients).Length == 0,
            "a conflict-only group does not register recipients from its healthy subset");
        check(!SpellDockRules.Describe("invis", buttons, clients).HasConflict && SpellDockRules.Describe("invis", buttons, clients).Available,
            "repairing the saved button restores its docked availability");
        var layout = ButtonColumns.Legacy(["invis", "assist", "heal", "cure"], 2);
        check(layout.Select(p => p.Column).SequenceEqual([0, 1, 0, 1]), "legacy dock placement preserves its original row-major columns");
        var legacyDock = Groups()[0] with { ButtonIds = ["invis", "assist", "heal", "cure"] };
        check(legacyDock.PositionedLayout.ToHashSet().SetEquals([new("invis", 0, 0), new("assist", 1, 0), new("heal", 0, 1), new("cure", 1, 1)]),
            "Legacy button IDs without saved coordinates open in their original rows and columns.");
        layout = ButtonColumns.Move(layout, "heal", 1, "assist");
        check(layout.Where(p => p.Column == 1).Select(p => p.ButtonId).SequenceEqual(["heal", "assist", "cure"]),
            "moving between columns preserves the requested within-column order");
        layout = ButtonColumns.Resize(layout, 1);
        check(layout.All(p => p.Column == 0) && layout.Select(p => p.ButtonId).SequenceEqual(["invis", "heal", "assist", "cure"]),
            "reducing columns appends displaced buttons in order without losing any references");
        var explicitGroup = Groups()[0].WithLayout([new("assist", 1), new("invis", 1)]);
        var explicitFile = output + ".columns.json";
        SpellOverlayStore.Save(explicitFile, new(3, [definition with { DockGroups = [explicitGroup] }]), null);
        check(SpellOverlayStore.Load(explicitFile).Library.Overlays.Single().DockGroups!.Single().Placements!
            .SequenceEqual(explicitGroup.Placements!), "explicit column assignments survive profile save and reload");
        Invalid(definition with { DockGroups = [explicitGroup with { Columns = 1 }] }, "out-of-range column references are rejected");
        Invalid(definition with { DockGroups = [explicitGroup with { Placements = [new("heal", 0)] }] }, "inconsistent membership and placement are rejected");
        Invalid(definition with { DockGroups = [explicitGroup with { Placements = [new("assist", 1, 2), new("invis", 1, 2)] }] },
            "Two saved buttons cannot occupy the same cell.");
        Invalid(definition with { DockGroups = [explicitGroup with { Placements = [new("assist", 1, -1), new("invis", 1, 0)] }] },
            "A negative saved row is rejected.");
        TestDockMoves(definition, check);
    }

    private static void TestDockMoves(SpellOverlayDefinition definition, Action<bool, string> check)
    {
        var source = new SpellDockGroup("travel", "Travel", SpellDockSide.Left, ["follow"], Scale: 1.2);
        var target = new SpellDockGroup("combat", "Combat", SpellDockSide.Top, ["assist", "heal"], Columns: 4,
            Placements: [new("assist", 0), new("heal", 2)]) { ColumnNames = ["First", "Empty", "Last", "Spare"] };
        var bar = definition with { DockGroups = [source, target] };
        var move = new SpellOverlayLayoutChange(ButtonMove: new(source, "follow", target, 1, "heal"));
        var moved = move.Apply(bar);
        var groups = moved.DockGroups!;
        check(groups[0] is { Id: "travel", Name: "Travel", Scale: 1.2, ButtonIds.Length: 0 } &&
            groups[1].ColumnNames!.SequenceEqual(["First", "Last"]) &&
            groups[1].Layout.SequenceEqual([new("assist", 0), new("follow", 1), new("heal", 1)]),
            "Dragging the final button out of a group preserves the empty group and inserts before the target in its displayed column.");

        bool Rejects(SpellOverlayDefinition current, SpellOverlayLayoutChange change)
        {
            try { change.Apply(current); return false; }
            catch (InvalidDataException) { return true; }
        }
        check(Rejects(moved, move), "A stale drop cannot move the button a second time.");
        var changedTarget = target.WithLayout([.. target.Layout, new("shield", 3)]);
        check(Rejects(bar with { DockGroups = [source, changedTarget] }, move),
            "A destination changed since the drag began rejects the drop.");
        var duplicate = target.WithLayout([.. target.Layout, new("follow", 2)]);
        check(Rejects(bar with { DockGroups = [source, duplicate] },
                new(ButtonMove: new(source, "follow", duplicate, 1, null))),
            "A drop into a group already using the saved button cannot remove its original placement.");
        var staggered = target.WithLayout([new("assist", 0, 0), new("heal", 2, 2)]);
        var staggeredBar = bar with { DockGroups = [source, staggered] };
        var fill = new SpellOverlayLayoutChange(ButtonMove: new(source, "follow", staggered, 1, null, Row: 1));
        var filled = fill.Apply(staggeredBar).DockGroups![1];
        check(filled.PositionedLayout.ToHashSet().SetEquals([new("assist", 0, 0), new("follow", 1, 1), new("heal", 1, 2)]) &&
            filled.ColumnNames!.SequenceEqual(["First", "Last"]),
            "Moving between groups into the middle cell of a sparse column preserves the surrounding rows and compacted column names.");
        check(Rejects(staggeredBar, new(ButtonMove: new(source, "follow", staggered, 1, null, Row: 2))) &&
            Rejects(staggeredBar with { DockGroups = [source, staggered.WithLayout([new("assist", 0, 0), new("heal", 2, 1)])] }, fill),
            "A drop into an occupied cell or a column whose rows changed during dragging is rejected.");
    }
}
