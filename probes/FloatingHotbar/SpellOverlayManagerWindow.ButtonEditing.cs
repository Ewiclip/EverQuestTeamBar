using W = System.Windows;

namespace TeamBar;

public partial class SpellOverlayManagerWindow
{
    internal Func<string> ButtonProfile = TeamBarApplication.DefaultProfile;

    private SpellOverlayWindow CreateFloatingOverlay(FloatingTheme theme) =>
        new(theme, SaveFloatingLayout, OpenOverlaySettings) { OpenButtonEditor = OpenFloatingButtonEditor };

    private void OpenFloatingButtonEditor(SpellOverlayWindow owner, W.FrameworkElement anchor, SpellDockSide side,
        string? groupId, string? buttonId, int column, bool newColumn, int? row)
    {
        if (owner.ButtonEditorOverlayId is not { } overlayId) return;
        var bar = EditableBar(owner, overlayId);
        var profile = ButtonProfile();
        var team = TeamEditing.Load(profile);
        var buttons = ReadDockButtons();
        var theme = FloatingThemes.ForApplication(preferences.Load()).Selected;
        SpellDockGroup? TargetGroup(SpellOverlayDefinition definition)
        {
            var groups = definition.DockGroups ?? [];
            if (groupId is not null)
                return groups.SingleOrDefault(g => g.Id == groupId && g.Side == side)
                    ?? throw new InvalidDataException("That button group moved. Open it again.");
            if (groups.Any(g => g.Side == side && g.ButtonIds.Length > 0))
                throw new InvalidDataException("This side now has buttons. Choose a row or column in its group.");
            return groups.FirstOrDefault(g => g.Side == side);
        }
        var group = TargetGroup(bar);

        void Save(OverlayButtonAction action, SavedButton? source = null, StandaloneButton? draft = null)
        {
            if (buttonId is null)
            {
                var current = TargetGroup(EditableBar(owner, overlayId));
                if (!SameColumnLayout(current, group))
                    throw new InvalidDataException("This group's layout changed. Choose the row or column again.");
            }
            SaveOverlayButton(owner, overlayId, profile, new(action, side, group?.Id, source, draft,
                Column: column, PlacementId: buttonId, NewColumn: newColumn, Row: row));
        }
        void Show(W.Window panel) => owner.ShowButtonEditor(panel, anchor, side);
        void ShowEditor(SavedButton? source)
        {
            var recipientTeam = team;
            foreach (var saved in buttons)
                recipientTeam = TeamEditing.IncludeButtonRecipients(recipientTeam, saved.Value);
            recipientTeam = TeamEditing.MergeDetected(recipientTeam, detected);
            var defaultTarget = recipientTeam.Characters.FirstOrDefault(c => c.Name.Equals(bar.Character, StringComparison.OrdinalIgnoreCase));
            var draft = source is null
                ? new StandaloneButton(1, "new-button", "", recipientTeam.Characters, recipientTeam.Personas,
                    defaultTarget is null ? [] : [new(new(CharacterId: defaultTarget.Id), "")], [], Floating: false)
                : source.Value with { Characters = recipientTeam.Characters, Personas = recipientTeam.Personas };
            int uses = source is null ? 0 : ButtonPlacementCount(source.Value, savedLibrary, team);
            Show(OverlayButtonEditor.CreateEditor(theme, draft, source is null, uses,
                edited => Save(source is null ? OverlayButtonAction.Create : OverlayButtonAction.EditSynced, source, edited),
                remove: source is null ? null : () => Save(OverlayButtonAction.Remove, source)));
        }

        if (buttonId is null)
        {
            string title = groupId is null ? "Add a button" : newColumn ? $"Start column {column + 1}" : $"Add to column {column + 1}";
            if (row is { } selectedRow) title += $", row {selectedRow + 1}";
            Show(OverlayButtonEditor.CreatePicker(theme, buttons, group?.ButtonIds ?? [],
                source => Save(OverlayButtonAction.Link, source), () => ShowEditor(null), title));
            return;
        }
        if (group?.ButtonIds.Contains(buttonId) != true) throw new InvalidDataException("This placement has changed. Open it again.");
        var source = buttons.FirstOrDefault(b => b.Value.Id == buttonId);
        if (source is null)
            Show(OverlayButtonEditor.CreateMissing(theme, () => Save(OverlayButtonAction.Remove)));
        else ShowEditor(source);
    }

    private static bool SameColumnLayout(SpellDockGroup? current, SpellDockGroup? original) =>
        ReferenceEquals(current, original) || (current is not null && original is not null && current.HasSameColumnLayout(original));

    private static bool SameDockGroups(SpellDockGroup[]? draft, SpellDockGroup[]? saved)
    {
        if (ReferenceEquals(draft, saved)) return true;
        draft ??= [];
        saved ??= [];
        return draft.Length == saved.Length && draft.Zip(saved).All(pair =>
            ReferenceEquals(pair.First, pair.Second) || (pair.First.Name == pair.Second.Name
                && pair.First.Scale == pair.Second.Scale && pair.First.ButtonIds.SequenceEqual(pair.Second.ButtonIds)
                && pair.First.HasSameColumnLayout(pair.Second)));
    }

    private SpellOverlayDefinition EditableBar(SpellOverlayWindow owner, string overlayId)
    {
        if (closed || review || !GameDataAccess.IsEnabled(MemoryFeature.SpellBarData) || !owner.IsVisible || owner.ButtonEditorOverlayId != overlayId
            || !floating.TryGetValue(overlayId, out var active) || !ReferenceEquals(owner, active))
            throw new InvalidOperationException("Reopen the button editor on the active spell bar.");
        return savedLibrary.Overlays.Single(d => d.Id == overlayId);
    }

    private static int ButtonPlacementCount(StandaloneButton button, SpellOverlayLibrary library, TeamDefinition team) =>
        library.Overlays.Sum(bar => bar.DockGroups?.Count(group => group.ButtonIds.Contains(button.Id)) ?? 0)
        + (team.Groups?.Count(group => group.Buttons.Any(p => p.ButtonId == button.Id)) ?? 0)
        + (button.Floating ? 1 : 0);

    private void SaveOverlayButton(SpellOverlayWindow owner, string overlayId, string profile, OverlayButtonChange change)
    {
        var bar = EditableBar(owner, overlayId);
        var setup = TeamBarApplication.Current?.OpenSetupForProfile(profile);
        if (change.Action == OverlayButtonAction.EditSynced && change.Source is { } source && setup?.HasPendingButtonEdit(source.Value.Id) == true)
            throw new InvalidOperationException("Finish the pending edits to this button in Settings before updating its shared shortcuts.");
        int index = definitions.ToList().FindIndex(d => d.Id == overlayId);
        if (index < 0 || !SameDockGroups(definitions[index].DockGroups, bar.DockGroups))
            throw new IOException("Finish the pending group changes in Settings, then reopen this button.");

        var result = SpellOverlayButtonEditing.Save(profile, path, savedLibrary, savedBytes, overlayId, change);
        savedBytes = result.Bytes; savedLibrary = result.Library;
        var latest = savedLibrary.Overlays.Single(d => d.Id == overlayId);
        // The edit is already on disk. Refresh the display if reconciliation fails.
        try
        {
            var editorDraft = definitions[index] with { DockGroups = latest.DockGroups };
            loading = true;
            try
            {
                definitions[index] = editorDraft;
                if (selected?.Id == overlayId) { selected = editorDraft; OverlayList.SelectedItem = editorDraft; }
            }
            finally { loading = false; }
            var allButtons = StandaloneButtons.Load(profile);
            var team = TeamEditing.Load(profile);
            dockReadGeneration++;
            savedDockButtons = allButtons;
            dockLibraryError = null;
            if (change.Action is OverlayButtonAction.Create or OverlayButtonAction.EditSynced)
            {
                setup?.ObserveSavedOverlayButton(allButtons.Single(b => b.Value.Id == result.ButtonId));
                AppInputSession.UpdateSavedConfiguration(profile, team, allButtons, preserveClearedHotkeys: true);
            }
            if (selected?.Id == overlayId) LoadDockEditor();
            RequestInputRosterRefresh();
            ReconcileSaved();
        }
        catch (Exception error)
        {
            nextDiscovery = DateTime.MinValue;
            applied = applied.Select(d => d.Id == overlayId ? latest : d).ToArray();
            Status("Button saved. Refreshing the display: " + error.Message, true);
        }
    }
}
