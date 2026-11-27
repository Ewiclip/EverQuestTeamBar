using System.Text.Json.Nodes;

namespace TeamBar;

internal static class LayoutLabProfileMigrationTests
{
    internal static void Run(string directory, Action<bool, string> check)
    {
        string lab = Path.Combine(directory, "lab"), normal = Path.Combine(directory, "normal"), receipts = Path.Combine(directory, "receipts");
        static string CreateProfile(string root, string relative)
        {
            string path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, """{"Version":1,"Characters":[],"Personas":[],"Groups":[]}""");
            return path;
        }
        var split = new GameViewLayout("Split", GameViewKind.EqualSplit, "DISPLAY-X", false, ["One", "Two"])
        {
            AvoidTaskbar = true, Windows = [new GameViewWindow(12, 34, 56), null]
        };
        string source = CreateProfile(lab, "team.json"), target = CreateProfile(normal, "team.json");
        var controls = Path.Combine(normal, "team.json.controls.screen-positions.json");
        File.WriteAllText(controls, "current positions");
        string originalTeam = File.ReadAllText(target);
        GameViewLayouts.Save(source, split);
        GameViewLayouts.Save(source, split with { Name = "new" });
        GameViewLayouts.Save(target, split with { Name = "SPLIT", Monitor = "DISPLAY-BASE" });
        File.WriteAllText(GameViewLayouts.PathFor(target), File.ReadAllText(GameViewLayouts.PathFor(target))
            .Replace("\"Name\": \"SPLIT\"", "\"FutureSetting\":true, \"Name\": \"SPLIT\""));
        var altSource = CreateProfile(lab, Path.Combine("other", "team.json"));
        var altTarget = CreateProfile(normal, Path.Combine("other", "team.json"));
        var alternate = split with { Name = "Alternate", SwapOnClick = true, MasterSlot = 1,
            Screens = [new("DISPLAY-X", GameViewKind.EqualSplit, 1), new("DISPLAY-Y", GameViewKind.Rows, 1)] };
        GameViewLayouts.Save(altSource, alternate);
        GameViewLayouts.Save(CreateProfile(lab, "lab-only.json"), split);
        var pristineSource = File.ReadAllBytes(GameViewLayouts.PathFor(source));

        LayoutLabProfileMigration.Import(lab, normal, receipts);
        var merged = GameViewLayouts.Load(target);
        var preserved = merged.Single(l => l.Name == "SPLIT");
        check(merged.Length == 2 && preserved.Monitor == "DISPLAY-BASE" &&
            File.ReadAllText(GameViewLayouts.PathFor(target)).Contains("FutureSetting"), "Base layouts win case-insensitive name conflicts, including their original fields.");
        var added = merged.Single(l => l.Name == "new");
        check(added.Monitor == split.Monitor && added.AvoidTaskbar && added.Characters.SequenceEqual(split.Characters) &&
            added.Windows is null, "Lab layouts keep their assigned characters and display settings, dropping process-specific window bindings.");
        var importedAlternate = GameViewLayouts.Load(altTarget).Single();
        check(importedAlternate.Name == "Alternate" && importedAlternate.MasterIndex == 1 &&
            importedAlternate.ScreenPlans.SequenceEqual(alternate.ScreenPlans) && !File.Exists(Path.Combine(normal, "lab-only.json")) &&
            !File.Exists(GameViewLayouts.PathFor(Path.Combine(normal, "lab-only.json"))),
            "Layouts imported into a matching named team retain all their screens and the explicit master seat.");
        check(File.ReadAllText(target) == originalTeam && File.ReadAllText(controls) == "current positions" &&
            File.ReadAllBytes(GameViewLayouts.PathFor(source)).SequenceEqual(pristineSource), "Migration preserves team controls, window positions, and the Lab source.");
        var before = File.ReadAllBytes(GameViewLayouts.PathFor(target));
        LayoutLabProfileMigration.Import(lab, normal, receipts);
        check(File.ReadAllBytes(GameViewLayouts.PathFor(target)).SequenceEqual(before), "Reopening base Team Bar leaves a migrated library untouched.");

        var contents = JsonNode.Parse(File.ReadAllText(GameViewLayouts.PathFor(target)))!.AsObject();
        contents["Layouts"]!.AsArray().RemoveAt(1);
        WindowLayouts.SaveJson(GameViewLayouts.PathFor(target), contents);
        LayoutLabProfileMigration.Import(lab, normal, receipts);
        check(GameViewLayouts.Load(target).Length == 1, "Removing an imported layout keeps it removed on later launches.");

        string lockedSource = CreateProfile(lab, "locked.json"), lockedTarget = CreateProfile(normal, "locked.json");
        GameViewLayouts.Save(lockedSource, split);
        bool blocked = false;
        using (File.Open(GameViewLayouts.PathFor(lockedTarget) + ".lock", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            try { LayoutLabProfileMigration.Import(lab, normal, receipts); }
            catch (InvalidDataException) { blocked = true; }
        }
        LayoutLabProfileMigration.Import(lab, normal, receipts);
        check(blocked && GameViewLayouts.Load(lockedTarget).Single().Name == "Split", "A competing layout writer prevents the migration from committing and it can retry.");

        string badSource = CreateProfile(lab, "bad.json"), badTarget = CreateProfile(normal, "bad.json");
        GameViewLayouts.Save(badSource, split);
        File.WriteAllText(GameViewLayouts.PathFor(badTarget), """{"Version":99,"Layouts":[]}""");
        string other = CreateProfile(normal, "healthy.json");
        GameViewLayouts.Save(CreateProfile(lab, "healthy.json"), split);
        bool rejected = false;
        try { LayoutLabProfileMigration.Import(lab, normal, receipts); }
        catch (InvalidDataException) { rejected = true; }
        check(rejected && File.ReadAllText(GameViewLayouts.PathFor(badTarget)) == """{"Version":99,"Layouts":[]}""" &&
            GameViewLayouts.Load(other).Length == 1, "An incompatible profile is preserved while other eligible profiles migrate.");
    }
}
