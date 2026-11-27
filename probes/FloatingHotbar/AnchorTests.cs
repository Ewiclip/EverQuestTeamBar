using System.Diagnostics;
using System.Text.Json;

internal static class AnchorTests
{
    internal static void Run(string output)
    {
        static void Check(bool value, string why) => StandaloneTests.Check(value, why);
        string directory = Path.GetFullPath(output) + ".fixtures";
        if (File.Exists(output) || Directory.Exists(directory)) throw new IOException("Test output must be new.");
        Directory.CreateDirectory(directory); string entity = Path.Combine(directory, "button.json");
        var large = new GameFrame("monitor-A", "Wide display", new(0, 0, 3440, 1440));
        var small = new GameFrame("monitor-B", "Small display", new(-1920, 20, 1920, 1040));
        var control = new Size(224, 118); var original = new Point(2810, 1130);
        var ratio = AnchorPositions.Normalize(original, control, large.Area);
        Check(AnchorPositions.Project(ratio, control, large.Area) == original, "Same-size placement did not round trip exactly.");
        var seeded = AnchorPositions.Seed(entity, "runa", ratio);
        Check(seeded.Screens.Length == 0, "Automatic initial fallback became an explicit monitor override.");
        var saved = AnchorPositions.Remember(entity, "runa", large, original, control);
        var projected = AnchorPositions.Resolve(saved, control, small);
        Check(small.Area.Contains(new Rectangle(projected, control)), "Fallback escaped a smaller negative-coordinate monitor.");
        Check(saved.Screens.Length == 1 && saved.Screens[0].ScreenId == "monitor-A", "Moving to an unknown screen created an override.");
        var smallOverride = new Point(-1800, 95);
        saved = AnchorPositions.Remember(entity, "runa", small, smallOverride, control);
        Check(AnchorPositions.Resolve(saved, control, large) == original, "An explicit placement on another screen replaced the original screen's override.");
        Check(AnchorPositions.Resolve(saved, control, small) == smallOverride, "Saved monitor placement did not beat fallback.");
        var translated = large with { Area = new(350, 230, large.Area.Width, large.Area.Height) };
        Check(AnchorPositions.Resolve(saved, control, translated) == new Point(original.X + 350, original.Y + 230), "Placement did not follow movement within the same monitor.");
        var tiny = large with { Area = new(500, 400, 400, 220) };
        Check(tiny.Area.Contains(new Rectangle(AnchorPositions.Resolve(saved, control, tiny), control)), "Resize projection escaped the game area.");
        Check(AnchorPositions.Resolve(saved, control, large) == original, "Repeated resizing drifted or overwrote the reference placement.");
        Check(AnchorPositions.Resolve(saved, new(1500, 500), tiny) == tiny.Area.Location, "Oversized control header must stay reachable.");
        AnchorPositions.Remember(entity, "fellwort", small, new(-1500, 400), control);
        var reloaded = AnchorPositions.Load(entity);
        Check(reloaded.Characters.Length == 2 && AnchorPositions.Resolve(AnchorPositions.Find(reloaded, "RUNA")!, control, large) == original, "Character histories were mixed or lost after reload.");
        string history = AnchorPositions.FileFor(entity); byte[] good = File.ReadAllBytes(history); File.WriteAllText(history, "{invalid");
        bool rejected = false; try { AnchorPositions.Remember(entity, "runa", large, original, control); } catch (JsonException) { rejected = true; }
        Check(rejected && File.ReadAllText(history) == "{invalid", "Unreadable position history was overwritten."); File.WriteAllBytes(history, good);

        using var process = Process.GetCurrentProcess();
        using (var fakeGame = new Form { Text = "Anchor geometry test · no game input", StartPosition = FormStartPosition.Manual, Bounds = new(180, 150, 700, 450) })
        {
            // A hidden test-process launch can suppress its first native ShowWindow.
            fakeGame.Show(); fakeGame.Hide(); fakeGame.Show(); Application.DoEvents();
            var detected = new DetectedClient(Environment.ProcessId, process.StartTime.ToUniversalTime().Ticks, fakeGame.Handle.ToInt64(), fakeGame.Left, new("Runa", 5, 130));
            var before = AnchorPositions.ReadFrame(detected); Check(before is not null, "Native client geometry was unavailable.");
            Check(before!.Area == fakeGame.RectangleToScreen(fakeGame.ClientRectangle), "Native geometry includes window chrome or has wrong screen coordinates.");
            fakeGame.Location = new(fakeGame.Left + 120, fakeGame.Top + 50); Application.DoEvents();
            var moved = AnchorPositions.ReadFrame(detected)!;
            Check(moved.Area.Location == new Point(before.Area.Left + 120, before.Area.Top + 50), "Native geometry did not follow the test window.");
            fakeGame.Size = new(580, 360); Application.DoEvents();
            Check(AnchorPositions.ReadFrame(detected)!.Area.Size == fakeGame.ClientSize, "Native geometry did not resize with the test window.");
            fakeGame.WindowState = FormWindowState.Minimized; Application.DoEvents(); Check(AnchorPositions.ReadFrame(detected) is null, "Minimized game windows must not supply an anchor.");
            fakeGame.WindowState = FormWindowState.Normal; Application.DoEvents(); Check(AnchorPositions.ReadFrame(detected) is not null, "Restored anchor was not found.");
            Check(AnchorPositions.ReadFrame(detected with { ProcessId = -1 }) is null, "Mismatched window ownership was accepted.");
            fakeGame.Close();
        }

        string profile = Path.Combine(directory, "team.json");
        var group = new ButtonGroup("position-group", "Position group", [new("follow", 0)]);
        var team = new TeamDefinition(1, [new("runa", "Runa"), new("fellwort", "Fellwort")],
            [new("runa-sk", "runa", "Shadowknight"), new("fellwort-shaman", "fellwort", "Shaman")], [], Groups: [group]);
        var standalone = new StandaloneButton(1, "follow", "Follow Runa", team.Characters, team.Personas, [new(new(CharacterId: "runa"), "2")], []);
        StandaloneButtons.Save(StandaloneButtons.PathFor(profile, standalone.Id), standalone, null);
        TeamEditing.Save(profile, team, []);
        DetectedClient[] clients = [new(11, 1, 111, 0, new("Runa", 5, 130), UserAssigned: true),
            new(22, 2, 222, 1000, new("Fellwort", 10, 130), UserAssigned: true)];
        new TeamBar.SetupWindow(profile, "test-controller.exe", () => new(clients, []), userSettings: new TeamBar.MemoryUserSettingsStore()).PlacementSelfTest();
        Check(TeamEditing.Load(profile).Groups!.Single(g => g.Id == group.Id).FollowCharacterId == "runa", "Group anchor selection did not persist.");
        Check(StandaloneButtons.Load(profile).Single().Value.FollowCharacterId == "fellwort", "Standalone anchor selection did not persist independently.");

        string runtime = Path.Combine(directory, "runtime", "team.json");
        var identity = new DetectedClient(Environment.ProcessId, process.StartTime.ToUniversalTime().Ticks, 1, 0, new("Runa", 5, 130), UserAssigned: true);
        team = team with { Groups = [group with { FollowCharacterId = "runa" }] };
        standalone = standalone with { FollowCharacterId = "runa" };
        StandaloneButtons.Save(StandaloneButtons.PathFor(runtime, standalone.Id), standalone, null);
        var live = new LiveSetup(Path.Combine(AppContext.BaseDirectory, "BarUiTestController.exe"), team, TeamEditing.Bind(team, [identity]), [identity]);
        using var journal = new StreamWriter(new FileStream(output + ".runtime.jsonl", FileMode.CreateNew, FileAccess.Write)) { AutoFlush = true };
        using var workspace = new FloatingWorkspace(live, StandaloneButtons.Load(runtime), runtime, journal);
        workspace.ReadIdentity = expected => expected; workspace.PlacementSelfTest();
        File.WriteAllText(output, JsonSerializer.Serialize(new { passed = true, tests = "proportional projection; saved-monitor precedence; negative coordinates; exact round-trip; move and resize without drift; oversized header; per-character history; corrupt history preservation; actual native client geometry and minimize/restore; bar and standalone UI persistence; runtime move/resize/monitor switch; native region relocation; minimize/unavailable hiding; independent drag saves; no implicit overrides; no tap or focus change", gameplayInput = false }));
    }
}
