using System.Text.Json;

internal static class GameViewLayoutTests
{
    internal static void Run(string output)
    {
        string root = Path.GetFullPath(output) + ".fixtures";
        if (File.Exists(output) || Directory.Exists(root)) throw new IOException("Choose a new game layout test output.");
        Directory.CreateDirectory(root);
        var checks = new List<string>();
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            checks.Add(description);
        }
        void Reject<T>(Action action, string description) where T : Exception
        {
            try { action(); }
            catch (T) { checks.Add(description); return; }
            throw new InvalidOperationException(description);
        }

        string liveFolder = Path.Combine(root, "Custom Live Folder");
        Directory.CreateDirectory(liveFolder);
        string channelFile = Path.Combine(liveFolder, "LaunchPad.ini");
        File.WriteAllText(channelFile, "[Settings]\nid=eq\nappName=EverQuest\n");
        Reject<InvalidDataException>(() => TeamBar.ScreenInstallation.Validate(liveFolder), "An alternate folder must contain eqgame.exe.");
        File.WriteAllBytes(Path.Combine(liveFolder, "eqgame.exe"), []);
        var store = new TeamBar.LocalUserSettingsStore(Path.Combine(root, "install-preferences.json"));
        store.SaveIconDirectory(root);
        store.SaveScreenDirectory(liveFolder + Path.DirectorySeparatorChar);
        var savedFolder = store.Load();
        Check(savedFolder.ScreenGameDirectory == liveFolder && savedFolder.IconGameDirectory == root &&
            !savedFolder.EnableScreenDetection && !savedFolder.EnableDataCollection && !savedFolder.EnableSpellBarData,
            "Selecting a Live layout folder preserves the artwork override and optional reader preferences.");
        foreach (var channel in new[] { "test", "beta" })
        {
            File.WriteAllText(channelFile, $"[Other]\nenvironment=live\n[Settings]\nappName=EverQuest\nid=eq\nenvironment={channel}\n");
            Reject<InvalidDataException>(() => TeamBar.ScreenInstallation.Validate(liveFolder), $"The {channel} launch channel is rejected.");
        }
        File.WriteAllText(channelFile, "[Other]\nid=eq\n[Settings]\nappName=EverQuest\n");
        Reject<InvalidDataException>(() => TeamBar.ScreenInstallation.Validate(liveFolder), "A game ID outside the launcher Settings section is ignored.");
        Check(TeamBar.ScreenInstallation.RecoveryName("layout", TeamBar.ScreenInstallation.Original) == "layout.json" &&
            TeamBar.ScreenInstallation.RecoveryName("layout", liveFolder) == TeamBar.ScreenInstallation.RecoveryName("layout", liveFolder.ToUpperInvariant()) &&
            TeamBar.ScreenInstallation.RecoveryName("layout", liveFolder) != TeamBar.ScreenInstallation.RecoveryName("layout", root),
            "Layout recovery remains compatible with previous installs and isolates each selected folder.");

        var monitor = new Rectangle(0, 0, 3440, 1440);
        var parked = new Rectangle(0, -31, 1720, 1479);
        var workspaceNormal = new Rectangle(-20, -71, 1720, 1479);
        var parkedClient = new Rectangle(8, 0, 1704, 1440);
        var restore = TeamBar.GameViewPlacements.ReachableRestoreBounds(parked, workspaceNormal, parkedClient, 0, [monitor]);
        Check(restore == (new Rectangle(0, 0, 1720, 1479), new Rectangle(-20, -40, 1720, 1479)),
            "A known parked title bar gets an on-screen restore point while retaining the normal placement's workspace offset.");
        Check(TeamBar.GameViewPlacements.ReachableRestoreBounds(parked, workspaceNormal, parkedClient, 0,
            [monitor, new Rectangle(0, -1440, 3440, 1440)]) == (parked, workspaceNormal),
            "A title bar visible on a monitor above keeps its original placement.");
        var corner = GameViewGeometry.Slots(GameViewKind.Corner, monitor, 3);
        Check(TeamBar.GameViewPlacements.OriginBeforeMaximize(new Size(1720, 1440), new Rectangle(-2560, -180, 2560, 1440))
            == new Point(-2140, -180), "A restored half-screen game moves onto the destination monitor before loading its maximized UI.");
        Check(TeamBar.GameViewPlacements.OriginBeforeMaximize(new Size(3440, 1440), new Rectangle(14, -1080, 1920, 1080))
            == new Point(-746, -1260), "A frame larger than its destination is centered across the smaller display without requesting a resize.");
        Check(corner.SequenceEqual([new(0, 0, 2294, 960), new(0, 960, 1146, 480), new(1146, 960, 1146, 480)]),
            "Three-slot corner matches the measured ultrawide arrangement.");
        Check(GameViewGeometry.Slots(GameViewKind.Corner, monitor, 2).SequenceEqual(corner.Take(2)),
            "The two-client corner retains both measured slot positions.");
        var offset = new Rectangle(-1919, -97, 1919, 1079);
        var halves = GameViewGeometry.Slots(GameViewKind.EqualSplit, offset, 2);
        Check(halves.SequenceEqual([new(-1919, -97, 959, 1079), new(-960, -97, 960, 1079)]),
            "An odd-sized monitor with a negative origin splits without a seam.");
        Reject<InvalidDataException>(() => GameViewGeometry.Slots(GameViewKind.EqualSplit, monitor, 7), "More than six anticipated slots is rejected.");
        var pair = GameViewGeometry.Slots(GameViewKind.Corner, offset, 2);
        Check(pair[0].Left == offset.Left && pair[0].Bottom == pair[1].Top && !pair.Any(s => s.Contains(offset.Right - 1, offset.Bottom - 1)),
            "A corner layout on a negative-origin display leaves its lower-right desktop accessible.");
        Reject<ArgumentOutOfRangeException>(() => GameViewGeometry.Slots(GameViewKind.Corner, Rectangle.Empty, 2),
            "A layout cannot be applied to an empty display.");
        foreach (var kind in Enum.GetValues<GameViewKind>())
        foreach (var edge in Enum.GetValues<GameViewEdge>())
        for (int count = 1; count <= 6; count++)
        {
            if (!GameViewGeometry.SupportsCount(kind, count)) continue;
            var cells = GameViewGeometry.Slots(kind, offset, count, edge);
            if (cells.Length != count || cells.Any(c => c.Width < 1 || c.Height < 1 || !offset.Contains(c)) ||
                cells.Where((cell, i) => cells.Skip(i + 1).Any(cell.IntersectsWith)).Any())
                throw new InvalidOperationException($"{kind}/{edge} has invalid or overlapping geometry for {count} slots.");
        }
        Check(true, "Every supported arrangement and edge keeps one distinct, nonempty rectangle per anticipated slot.");
        var grid = GameViewGeometry.Slots(GameViewKind.Grid, offset, 5);
        Check(grid[0].Top == offset.Top && grid[2].Right == offset.Right && grid[3].Top == grid[0].Bottom &&
            grid[4].Bottom == offset.Bottom && grid.All(c => !c.Contains(offset.Right - 1, offset.Bottom - 1)),
            "A five-slot grid numbers rows from the top left and leaves the unused sixth cell available to the desktop.");
        foreach (var edge in Enum.GetValues<GameViewEdge>())
        {
            var cells = GameViewGeometry.Slots(GameViewKind.MainStrip, offset, 4, edge);
            bool adjacent = cells.Skip(1).All(preview => edge switch
            {
                GameViewEdge.Top => preview.Bottom == cells[0].Top && preview.Top == offset.Top,
                GameViewEdge.Bottom => preview.Top == cells[0].Bottom && preview.Bottom == offset.Bottom,
                GameViewEdge.Left => preview.Right == cells[0].Left && preview.Left == offset.Left,
                _ => preview.Left == cells[0].Right && preview.Right == offset.Right
            });
            Check(adjacent, $"The main view and {edge} strip use the selected edge.");
        }
        Reject<ArgumentOutOfRangeException>(() => GameViewGeometry.Slots(GameViewKind.Rows, new Rectangle(0, 0, 100, 2), 6),
            "A monitor too short to fit a row for each character is rejected.");

        string profile = Path.Combine(root, "profiles", "team.json");
        string unrelated = Path.Combine(root, "other-team.json");
        string sidecar = GameViewLayouts.PathFor(profile);
        var original = new GameViewLayout("Raid", GameViewKind.Corner, "Display 1", true, ["Tank", "Cleric"]);
        var second = new GameViewLayout("Duo", GameViewKind.EqualSplit, "Display 2", false, ["Druid", "Bard"]);
        Check(GameViewLayouts.Load(profile).Length == 0, "An existing profile without layouts opens with an empty list.");
        GameViewLayouts.Save(profile, original);
        GameViewLayouts.Save(profile, second);
        GameViewLayouts.Save(profile, original with { Name = " raid ", SwapOnClick = false, Characters = ["Cleric", "Tank", "Bard"] });
        var saved = GameViewLayouts.Load(profile);
        Check(saved.Length == 2 && saved[0].Name == "raid" && !saved[0].SwapOnClick
            && saved[0].Characters.SequenceEqual(["Cleric", "Tank", "Bard"]), "Saving a matching layout name updates the existing slot order and swap mode.");
        Check(saved[1].Name == second.Name && saved[1].Kind == second.Kind && saved[1].Monitor == second.Monitor
            && saved[1].Characters.SequenceEqual(second.Characters), "Saving one layout preserves its named sibling.");
        Check(!File.Exists(profile) && GameViewLayouts.Load(unrelated).Length == 0, "Layout storage is independent of team profiles and other teams.");

        var bard = new GameSession(11, 1001, 91, 0, "Window A");
        var druid = new GameSession(12, 1002, 92, 0, "Window B");
        var mixed = new GameViewLayout("Desk", GameViewKind.Rows, "Left", true, ["", "Bard", "", "", "Druid", "", "", ""])
        {
            Screens = [new("Left", GameViewKind.Rows, 2, MonitorId: "Panel A"), new("Upper", GameViewKind.MainStrip, 6, GameViewEdge.Right, "Panel B")],
            MasterSlot = 4, Windows = [null, GameViewWindow.From(bard), null, null, GameViewWindow.From(druid), null, null, null],
            AvoidTaskbar = true, AlwaysOnTop = true, MatchMasterResolution = false, HideTitleBars = true
        };
        string deskProfile = Path.Combine(root, "desk.json");
        GameViewLayouts.Save(deskProfile, mixed);
        var desk = GameViewLayouts.Load(deskProfile).Single();
        Check(!desk.MatchMasterResolution, "A saved multi-monitor preset restores its per-slot resolution preference.");
        Check(desk.HideTitleBars, "A multi-monitor preset saves one title bar preference for the whole layout.");
        Check(!desk.AddSwapButton && desk.SwapButtonStyle == GameViewSwapButtonStyle.CharacterName && desk.SwapButtonLabel("Bard") == "Bard",
            "Existing presets keep their Quick Swap behavior and start with character-name buttons disabled.");
        using (var document = JsonDocument.Parse(File.ReadAllText(GameViewLayouts.PathFor(deskProfile))))
            Check(document.RootElement.GetProperty("Version").GetInt32() == 3,
                "A screen plan uses v3 so older Team Bar builds cannot overwrite its monitor assignments.");
        Check(desk.ScreenPlans.SequenceEqual(mixed.ScreenPlans) && desk.MasterIndex == 4 && desk.SwapOnClick &&
            desk.Characters.SequenceEqual(mixed.Characters) && desk.Windows!.SequenceEqual(mixed.Windows!) &&
            desk.AvoidTaskbar && desk.AlwaysOnTop,
            "A multi-monitor layout saves screen order, independent arrangements, master seat and all eight home slots.");
        var assignments = desk.Resolve([druid, bard], []);
        Check(assignments[1] == bard && assignments[4] == druid && assignments.Count(a => a is not null) == 2,
            "An explicit window on the second screen keeps its home slot across restart, including empty slots before it.");
        var upper = new Rectangle(401, -1717, 2561, 1439);
        Rectangle? Area(string device) => device switch { "Left" => offset, "Upper" => upper, _ => null };
        var deskSlots = GameViewGeometry.Slots(desk, Area);
        Check(deskSlots.SequenceEqual(GameViewGeometry.Slots(GameViewKind.Rows, offset, 2)
                .Concat(GameViewGeometry.Slots(GameViewKind.MainStrip, upper, 6, GameViewEdge.Right))),
            "Each monitor uses its own physical area and arrangement across gaps, unequal sizes and negative desktop coordinates.");
        Check(GameViewGeometry.Slots(desk with { SwapOnClick = false, MasterSlot = null }, Area).SequenceEqual(deskSlots),
            "Fixed and master-swap policies use the same home slot geometry.");
        var leftPanel = new GameViewMonitor("Upper", offset, offset with { Height = offset.Height - 40 }, MonitorId: "panel a");
        var upperPanel = new GameViewMonitor("Left", upper, upper with { Height = upper.Height - 48 }, MonitorId: "Panel B");
        GameViewMonitor[] renumbered = [upperPanel, leftPanel];
        var spareBounds = new Rectangle(upper.Right + 60, 20, 1080, 1920);
        var sparePanel = new GameViewMonitor("Portrait", spareBounds, spareBounds, MonitorId: "Panel C");
        Check(GameViewGeometry.Slots(desk, [sparePanel, upperPanel, leftPanel]).SequenceEqual(GameViewGeometry.Slots(desk, renumbered)),
            "Connecting or removing an unused monitor leaves all active screen slots and the master in place.");
        Check(GameViewMonitor.Find(desk.ScreenPlans[0], renumbered) == leftPanel &&
            GameViewGeometry.Slots(desk with { AvoidTaskbar = false }, renumbered).SequenceEqual(deskSlots),
            "Selected monitors keep their home slots when Windows exchanges their display numbers and enumeration order.");
        Check(GameViewGeometry.Slots(desk, renumbered).SequenceEqual(GameViewGeometry.Slots(GameViewKind.Rows, leftPanel.WorkArea, 2)
            .Concat(GameViewGeometry.Slots(GameViewKind.MainStrip, upperPanel.WorkArea, 6, GameViewEdge.Right))),
            "The taskbar setting uses each resolved physical monitor's working area.");
        Reject<InvalidOperationException>(() => GameViewGeometry.Slots(desk, [upperPanel]),
            "A different panel reusing the disconnected panel's old display number cannot take over its slots.");
        Check(GameViewMonitor.Find(desk.ScreenPlans[0], [leftPanel, leftPanel with { Device = "Third" }]) is null,
            "An ambiguous monitor identity is not guessed.");
        Check(GameViewMonitor.Find(desk.ScreenPlans[0] with { MonitorId = null }, renumbered) == upperPanel,
            "Older layouts without a physical ID still resolve by their saved Windows display name.");
        var reusedNumber = desk with { Screens = [desk.ScreenPlans[0], desk.ScreenPlans[1] with { Monitor = "Left" }] };
        Check(GameViewGeometry.Slots(reusedNumber, renumbered).SequenceEqual(GameViewGeometry.Slots(desk, renumbered)),
            "Two known panels can retain the same historical display number during reconnection without losing assignments.");
        Reject<InvalidOperationException>(() => GameViewGeometry.Slots(desk, name => name == "Upper" ? null : Area(name)),
            "A disconnected selected monitor is reported without remapping its seats to another monitor.");
        Reject<InvalidOperationException>(() => GameViewGeometry.Slots(desk, _ => upper),
            "Mirrored or overlapping screen areas cannot place two clients into the same desktop space.");
        var deskBytes = File.ReadAllBytes(GameViewLayouts.PathFor(deskProfile));
        Reject<InvalidDataException>(() => GameViewLayouts.Save(deskProfile, desk with { MasterSlot = null }),
            "Swapping across screens requires an explicit master destination.");
        Reject<InvalidDataException>(() => GameViewLayouts.Save(deskProfile, desk with { MasterSlot = 8 }),
            "The master slot must belong to a selected screen.");
        var fixedProfile = Path.Combine(root, "fixed.json");
        GameViewLayouts.Save(fixedProfile, desk with { SwapOnClick = false });
        var fixedDesk = GameViewLayouts.Load(fixedProfile).Single();
        Check(fixedDesk.MasterIndex == -1 && (fixedDesk with { SwapOnClick = true }).MasterIndex == 4,
            "Disabling swapping retains the chosen destination through a save and reload.");
        Reject<InvalidDataException>(() => GameViewLayouts.Save(deskProfile, desk with
            { Screens = [desk.ScreenPlans[0], desk.ScreenPlans[1] with { Monitor = " left ", MonitorId = null }] }),
            "A name-only screen assignment cannot ambiguously reuse another monitor's display number.");
        Reject<InvalidDataException>(() => GameViewLayouts.Save(deskProfile, desk with
            { Screens = [desk.ScreenPlans[0], desk.ScreenPlans[1] with { MonitorId = " PANEL A " }] }),
            "Distinct display numbers cannot reserve the same physical monitor twice.");
        Reject<InvalidDataException>(() => GameViewLayouts.Save(deskProfile, desk with
            { Screens = [desk.ScreenPlans[0], desk.ScreenPlans[1] with { Count = 5 }] }),
            "The sum of the screen capacities must match the character and window assignments.");
        Reject<InvalidDataException>(() => GameViewLayouts.Save(deskProfile, desk with
            { Characters = ["Bard", "", "", "", "", "", "", "bard"] }),
            "A character cannot be assigned to seats on two different screens.");
        Reject<InvalidDataException>(() => GameViewLayouts.Save(deskProfile, desk with
            { Windows = [GameViewWindow.From(bard), null, GameViewWindow.From(bard), null, null, null, null, null] }),
            "Two slots on different screens cannot explicitly claim the same EQ window.");
        Check(File.ReadAllBytes(GameViewLayouts.PathFor(deskProfile)).SequenceEqual(deskBytes),
            "Validation failures leave a saved multi-monitor layout intact.");
        var manualSwap = desk with { Name = "Button swap", SwapOnClick = false, AddSwapButton = true,
            SwapButtonStyle = GameViewSwapButtonStyle.CustomText, SwapButtonText = " Play {character} " };
        GameViewLayouts.Save(deskProfile, manualSwap);
        var buttonPreset = GameViewLayouts.Load(deskProfile).Single(l => l.Name == manualSwap.Name);
        Check(buttonPreset.MasterIndex == -1 && buttonPreset.SwapTargetIndex == 4 && buttonPreset.SwapButtonLabel("Druid") == "Play Druid" &&
            GameViewLayouts.FileVersion([buttonPreset]) == 4, "Button-only swapping saves its star, independent mode and character text template.");
        Reject<InvalidDataException>(() => GameViewLayouts.Normalize(buttonPreset with { MasterSlot = null }),
            "Button-only swapping across monitors requires a starred destination.");
        Reject<InvalidDataException>(() => GameViewLayouts.Normalize(buttonPreset with { SwapButtonText = "one\ntwo" }),
            "Custom swap button text must be one line.");
        GameViewLayouts.Save(deskProfile, original with { Name = "Second seat master", MasterSlot = 1 });
        var alongside = GameViewLayouts.Load(deskProfile);
        var secondSeatMaster = alongside.Single(l => l.Name == "Second seat master");
        Check(alongside[0].ScreenPlans.SequenceEqual(desk.ScreenPlans) && secondSeatMaster.ScreenPlans.Length == 1 && secondSeatMaster.MasterIndex == 1,
            "A single-screen preset can choose its second slot as master alongside a multi-screen preset.");
        static DetectedClient Identity(GameSession w, string name) => new(w.ProcessId, w.StartedUtcTicks, w.Window, w.Left, new(name, 1, 100));
        var party = new GameViewLayout("Five slots", GameViewKind.MainStrip, "Display 1", false, ["Bard", "", "Druid", "", ""])
            { Edge = GameViewEdge.Left, AvoidTaskbar = true };
        GameViewLayouts.Save(profile, party);
        var empty = party with { Name = "Unassigned", Characters = new string[] { "", "", "", "", "" } };
        GameViewLayouts.Save(profile, empty);
        var updated = GameViewLayouts.Load(profile);
        Check(updated[2].Characters.SequenceEqual(party.Characters) && updated[2].Edge == party.Edge && updated[2].AvoidTaskbar &&
            updated[3].Resolve([bard, druid], [Identity(bard, "Bard"), Identity(druid, "Druid")]).All(s => s is null),
            "Slot capacity, unassigned seats, strip edge and taskbar preference survive a save without claiming open games.");
        var alone = party.Resolve([druid], [Identity(druid, "Druid")]);
        Check(alone.SequenceEqual(new GameSession?[] { null, null, druid, null, null }), "A lone online character stays in its third slot.");
        var both = party.Resolve([druid, bard], [Identity(bard, "BARD"), Identity(druid, "Druid")], alone);
        Check(both.SequenceEqual(new GameSession?[] { bard, null, druid, null, null }), "A login fills its saved slot while existing and empty seats stand still.");
        var conflicting = party with { Windows = [null, GameViewWindow.From(bard), null, null, null] };
        Check(conflicting.Resolve([bard, druid], [Identity(bard, "Bard"), Identity(druid, "Druid")])
            .SequenceEqual(new GameSession?[] { null, bard, druid, null, null }), "An explicit manual assignment takes priority over a competing name match.");
        var samePidNewProcess = bard with { StartedUtcTicks = 1004 };
        Check(conflicting.Resolve([samePidNewProcess], []).All(s => s is null), "A reused process ID cannot claim a manual slot from a previous game session.");
        var twins = new[] { Identity(bard, "Bard"), Identity(druid, "Bard") };
        Check(party.Resolve([bard, druid], twins)[0] is null && party.Resolve([bard, druid], twins, both)[0] == bard,
            "A name shared by two games needs a manual selection unless the same game already holds that slot.");

        long clock = 2000;
        var cache = new GameViewRosterCache(party, () => clock);
        GameSession[] opened = [bard, druid];
        Discovery identified = new([Identity(bard, "Bard"), Identity(druid, "Druid")], []);
        Discovery UnexpectedScan() => throw new InvalidOperationException("This seat assignment does not need identity discovery.");
        var namedPin = new GameViewRosterCache(party with { Windows = [GameViewWindow.From(bard), null, null, null, null] });
        Check(new GameViewRosterCache(empty).ResolveOpenWindows(opened, UnexpectedScan, []).All(c => c is null) &&
            namedPin.ResolveOpenWindows([bard], UnexpectedScan, [])[0] == bard &&
            cache.ResolveOpenWindows([], UnexpectedScan, []).All(c => c is null),
            "Empty layouts, empty rosters, and fully claimed live windows skip character discovery.");
        Check(namedPin.ResolveOpenWindows([druid], () => identified, [])[2] == druid &&
            namedPin.ResolveOpenWindows([samePidNewProcess], () => new([Identity(samePidNewProcess, "Bard")], []), [])[0] == samePidNewProcess,
            "An available window still resolves a name after another client leaves or an explicit window's process restarts.");
        Discovery interrupted = new([Identity(druid, "Druid")], ["Character identity unavailable"]);
        var initialSeats = cache.Resolve(opened, identified, both);
        clock += GameViewRosterCache.GraceMilliseconds - 1;
        Check(cache.Resolve(opened, interrupted, initialSeats).SequenceEqual(initialSeats),
            "A transient discovery error keeps a recently verified character in its exact open window and preserves its neighbors.");
        clock++;
        Check(cache.Resolve(opened, interrupted, initialSeats)[0] is null,
            "Consecutive errors do not extend the grace period beyond the last confirmed identity.");
        cache.Resolve(opened, identified, initialSeats);
        Check(cache.Resolve([druid, samePidNewProcess], interrupted, initialSeats)[0] is null,
            "An exact window leaving invalidates roster grace immediately even when its PID is reused.");
        cache.Resolve(opened, identified, initialSeats);
        Check(cache.Resolve(opened, new([Identity(bard, "Warrior")], ["Unrelated read failure"]), initialSeats)[0] is null,
            "A positively identified different character immediately vacates the former character's seat.");
        cache.Resolve(opened, identified, initialSeats);
        Check(cache.Resolve(opened, new([], []), initialSeats)[0] is null,
            "A completed discovery without an error clears a missing character without grace.");
        Check(new GameViewRosterCache(party).Resolve(opened, interrupted, initialSeats)[0] is null,
            "The cache cannot assign a previously unseen character from a stale previous-seat hint.");

        string oldProfile = Path.Combine(root, "original.json");
        string oldSidecar = GameViewLayouts.PathFor(oldProfile);
        var originalBytes = """{"Version":1,"Layouts":[{"Name":"Original","Kind":"EqualSplit","Monitor":"Display 1","SwapOnClick":true,"Characters":["Warrior","Druid"],"Windows":[{"ProcessId":11,"StartedUtcTicks":1001,"Window":91},null]}]}""";
        File.WriteAllText(oldSidecar, originalBytes);
        var legacy = GameViewLayouts.Load(oldProfile).Single();
        Check(legacy.MatchMasterResolution, "Presets without a resolution preference default to matching the master.");
        Check(File.ReadAllText(oldSidecar) == originalBytes && legacy.Windows?[0] == GameViewWindow.From(bard) &&
            legacy.Characters.SequenceEqual(["Warrior", "Druid"]) && legacy.SwapOnClick && !legacy.AvoidTaskbar && !legacy.AlwaysOnTop &&
            GameViewGeometry.Slots(legacy.Kind, offset, legacy.Characters.Length, legacy.Edge).SequenceEqual(halves),
            "Opening a v1 layout preserves its resolution, order, manual window and original file.");
        GameViewLayouts.Save(oldProfile, empty);
        using (var doc = JsonDocument.Parse(File.ReadAllText(oldSidecar)))
            Check(doc.RootElement.GetProperty("Version").GetInt32() == 2 && GameViewLayouts.Load(oldProfile)[0].Windows?[0] == GameViewWindow.From(bard),
                "The next save atomically upgrades to v2 while preserving the legacy layout beside new empty slots.");
        string frameProfile = Path.Combine(root, "frames.json");
        File.WriteAllText(GameViewLayouts.PathFor(frameProfile), """
            {"Version":2,"Layouts":[
              {"Name":"Fixed","Kind":"EqualSplit","Monitor":"Display 1","ShowTitleBars":false,"SwapOnClick":false,"Characters":["",""]},
              {"Name":"Swap","Kind":"Corner","Monitor":"Display 1","ShowTitleBars":true,"SwapOnClick":true,"Characters":["",""]}]}
            """);
        var frames = GameViewLayouts.Load(frameProfile);
        Check(frames.All(layout => !layout.HideTitleBars),
            "Existing presets keep their title bars regardless of their obsolete ShowTitleBars setting.");
        Check(frames[0].ScreenPlans.SequenceEqual([new GameViewScreen("Display 1", GameViewKind.EqualSplit, 2)]) &&
            frames[0].MasterIndex == -1 && frames[1].MasterIndex == 0, "Single-monitor saves keep their original fixed or first-seat master behavior.");
        GameViewLayouts.Save(frameProfile, frames[1] with { AlwaysOnTop = true });
        frames = GameViewLayouts.Load(frameProfile);
        Check(!File.ReadAllText(GameViewLayouts.PathFor(frameProfile)).Contains("ShowTitleBars") &&
            !frames[0].SwapOnClick && frames[1].SwapOnClick && frames[1].AlwaysOnTop,
            "Resaving a legacy layout retires its frame override and preserves both presets' swap settings.");
        var good = File.ReadAllBytes(sidecar);
        Reject<InvalidDataException>(() => GameViewLayouts.Save(profile, original with { Characters = ["Tank", "tank"] }),
            "A second assignment of the same character is rejected.");
        using (File.Open(sidecar + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Reject<IOException>(() => GameViewLayouts.Save(profile, original), "A competing writer cannot overwrite the layouts.");
        Check(File.ReadAllBytes(sidecar).SequenceEqual(good), "Failed saves preserve the existing layout file.");

        TeamEditing.Save(profile, new(1, [], [], []), []);
        var positions = new GameViewSwapButtonPositions(profile);
        var upperOffset = new RelativePosition(.26, .81);
        positions.Save(buttonPreset, 4, upperOffset); // third slot on the second monitor
        new GameViewSwapButtonPositions(profile).Save(buttonPreset, 0, new(.5, .5));
        positions.Save(buttonPreset, 5, new(.1, .2));
        var reordered = buttonPreset with { Screens = [buttonPreset.ScreenPlans[1] with { Monitor = "Renumbered" }, buttonPreset.ScreenPlans[0]] };
        Check(positions.Read(reordered, 2) == upperOffset && positions.Read(reordered, 6) == new RelativePosition(.5, .5) &&
            positions.Read(reordered with { Name = "Other preset" }, 2) is null,
            "A dragged position stays on its slot after a monitor reorder or renumber and preserves other writers' placements.");
        Reject<ArgumentOutOfRangeException>(() => positions.Save(buttonPreset, 0, new(double.NaN, .5)), "Invalid button positions cannot replace saved positions.");
        var imported = Path.Combine(root, "imported");
        TeamBar.ProfileMigration.CopyValidatedLibrary(Path.GetDirectoryName(profile)!, imported, normalize: true);
        Check(File.ReadAllBytes(GameViewLayouts.PathFor(Path.Combine(imported, "team.json"))).SequenceEqual(good),
            "A profile import preserves named layouts, slot order and mode.");
        Check(!File.Exists(GameViewLayouts.PathFor(Path.Combine(imported, "team.json")) + ".lock"),
            "The transient writer lock stays out of a profile import.");
        Check(new GameViewSwapButtonPositions(Path.Combine(imported, "team.json")).Read(reordered, 2) == upperOffset,
            "Profile imports carry swap button positions across restarts.");

        File.WriteAllText(sidecar, """{"Version":99,"Layouts":[]}""");
        Reject<InvalidDataException>(() => GameViewLayouts.Save(profile, original), "An unsupported data version cannot be overwritten.");
        Check(File.ReadAllText(sidecar).Contains("\"Version\":99", StringComparison.Ordinal), "The unsupported sidecar survives the failed save.");
        File.WriteAllText(sidecar, "{");
        Reject<InvalidDataException>(() => GameViewLayouts.Load(profile), "A corrupt sidecar is reported.");
        EqLaunchPositionTests.Run(root, Check);
        TeamBar.LayoutLabProfileMigrationTests.Run(root, Check);
        GameViewLaunchGuardTests.Run(root, Check);
        using var result = File.Open(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(result, new { passed = true, checks });
    }
}
