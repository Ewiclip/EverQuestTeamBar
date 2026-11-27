using W = System.Windows;
using C = System.Windows.Controls;
using M = System.Windows.Media;

namespace TeamBar;

internal static class MappingConflictEditorTests
{
    internal static void Run(string directory)
    {
        using var access = GameDataAccess.FixtureScope(false);
        Directory.CreateDirectory(directory);
        string profile = Path.Combine(directory, "team.json");
        var team = new TeamDefinition(1, [new("maple", "Maple"), new("cedar", "Cedar")],
            [new("maple-shaman", "maple", "Shaman"), new("cedar-cleric", "cedar", "Cleric")], []);
        TeamEditing.Save(profile, team, []);
        var conflict = new StandaloneButton(1, "conflicted", "Needs a recipient choice", team.Characters, team.Personas,
            [new(new(PersonaId: "maple-shaman"), "1"), new(new(CharacterId: "maple", Class: "Shaman"), "2")], []);
        var normal = conflict with { Id = "normal", Label = "Working sequence", Rules = [new(new(CharacterId: "cedar"), "1"), new(new(CharacterId: "cedar"), "2")] };
        foreach (var button in new[] { conflict, normal }) StandaloneButtons.Save(StandaloneButtons.PathFor(profile, button.Id), button, null);
        DetectedClient[] clients = [new(11, 1, 111, 0, new("Maple", 10, 130), true), new(22, 2, 222, 1000, new("Cedar", 2, 130), true)];
        var setup = new SetupWindow(profile, "no-input-fixture", () => new(clients, []), userSettings: new MemoryUserSettingsStore());
        try { setup.MappingConflictSelfTest(directory); }
        finally { setup.CloseUsabilityFixture(); }
    }
}

public partial class SetupWindow
{
    internal void MappingConflictSelfTest(string directory)
    {
        SynchronizationContext.SetSynchronizationContext(new W.Threading.DispatcherSynchronizationContext(Dispatcher));
        Show(); Settle();
        long until = Environment.TickCount64 + 5000;
        while (refreshing && Environment.TickCount64 < until) { Settle(); Thread.Sleep(5); }
        ShowStandaloneLibrary("conflicted"); Settle();
        var conflicted = selected!;
        var persisted = File.ReadAllBytes(StandaloneButtons.PathFor(profilePath, conflicted.Id));
        StandaloneTests.Check(clients.Count == 2 && conflicted.HasConflict && rows.All(row => row.HasConflict)
            && ButtonConflictMessage.IsVisible && ButtonConflictMessage.Text.Contains("selected characters"),
            "A saved tied-selector conflict must open normally and explain the disabled button and affected rows.");
        StandaloneTests.Check(SaveControl.IsEnabled && ApplyControl.IsEnabled && !conflicted.Dirty
            && !standaloneDrafts.Single(draft => draft.Id == "normal").HasConflict,
            "A conflict must keep editing, save, and connect available without flagging an unrelated ordered sequence.");
        var errorColor = ((M.SolidColorBrush)FindResource("Error")).Color;
        var name = SettingsDescendants(ButtonList).OfType<C.TextBlock>().Single(text => text.Name == "LibraryButtonName" && text.DataContext == conflicted);
        var borders = SettingsDescendants(RuleList).OfType<C.Border>().Where(border => border.Name == "MappingRule").ToArray();
        StandaloneTests.Check(((M.SolidColorBrush)name.Foreground).Color == errorColor && borders.Length == 2
            && borders.All(border => border.BorderThickness == new W.Thickness(2) && ((M.SolidColorBrush)border.BorderBrush).Color == errorColor),
            "The rendered library name and both conflicting mapping borders must use the red error color.");
        RenderSettings(Path.Combine(directory, "conflict-dark.png"));
        clients.Single(client => client.Name == "Maple").Included = false; Settle();
        StandaloneTests.Check(conflicted.HasConflict && ButtonConflictMessage.Text.Contains("saved persona") && ApplyControl.IsEnabled
            && !conflicted.Dirty && persisted.SequenceEqual(File.ReadAllBytes(StandaloneButtons.PathFor(profilePath, conflicted.Id))),
            "An inactive conflict remains visible without disabling other clients, rewriting mappings, or marking diagnostic refreshes dirty.");
        rows[1].Target = rows[0].Target; Settle();
        StandaloneTests.Check(!conflicted.HasConflict && rows.All(row => !row.HasConflict) && !ButtonConflictMessage.IsVisible
            && SaveControl.IsEnabled && FlushAutoSave(), "Using the same recipient for both keys must clear the conflict and save the ordered sequence.");
        var saved = StandaloneButtons.Load(profilePath).Single(button => button.Value.Id == conflicted.Id).Value;
        StandaloneTests.Check(saved.Rules.Select(rule => rule.Key).SequenceEqual(["1", "2"])
            && saved.Rules.Select(rule => rule.Target).Distinct().Count() == 1, "Fixing the recipient must preserve both ordered keypresses.");
    }
}
