using W = System.Windows;
using C = System.Windows.Controls;

namespace TeamBar;

public partial class SpellOverlayManagerWindow
{
    private TargetCapture? pickerCapture;
    private (string Id, string Character, string? Class)? pickerOwner;
    private bool changingSource;
    private void ResetPickerForBar()
    {
        var owner = selected is null ? ((string, string, string?)?)null : (selected.Id, selected.Character, selected.PersonaClass);
        if (owner == pickerOwner) return;
        pickerOwner = owner; pickerCapture = null; lookupGeneration++;
        if (initialized && resultsFromTarget) RefreshSearchResults();
    }
    private void Search_Changed(object sender, C.TextChangedEventArgs e) { if (initialized) RefreshSearchResults(); }
    private void CasterFilter_Changed(object sender, W.RoutedEventArgs e) { if (initialized && resultsFromTarget) RefreshSearchResults(); }
    private async void Source_Changed(object sender, C.SelectionChangedEventArgs e)
    {
        if (!initialized || changingSource) return;
        resultsFromTarget = SpellSourceChoice.SelectedIndex == 1; lookupGeneration++; pickerCapture = null;
        RefreshSearchResults();
        if (resultsFromTarget && !review) await ReadTargetForPicker();
    }
    private async void CurrentTarget_Click(object sender, W.RoutedEventArgs e)
    {
        changingSource = true; SpellSourceChoice.SelectedIndex = 1; changingSource = false;
        resultsFromTarget = true;
        if (!review) await ReadTargetForPicker(); else RefreshSearchResults();
    }
    private async Task ReadTargetForPicker()
    {
        if (selected is null) return;
        var owner = pickerOwner; int request = ++lookupGeneration;
        pickerCapture = null; RefreshSearchResults(); SearchHint.Text = "Reading current target…";
        var clients = detected.Where(c => c.Character.Name.Equals(selected.Character, StringComparison.OrdinalIgnoreCase)).ToArray();
        try
        {
            if (clients.Length != 1) throw new InvalidDataException("Load exactly one matching character, then read the target again.");
            var capture = await Task.Run(() => CaptureTarget(clients[0].ProcessId, clients[0].StartedUtcTicks));
            if (closed || pickerOwner != owner || request != lookupGeneration || !resultsFromTarget) return;
            AcceptPickerCapture(capture);
        }
        catch (Exception error)
        {
            if (!closed && pickerOwner == owner && request == lookupGeneration && resultsFromTarget)
            { pickerCapture = null; RefreshSearchResults(); SearchHint.Text = "Target spells unavailable · " + error.Message; }
        }
    }
    private void AcceptPickerCapture(TargetCapture capture)
    {
        if (selected is null || !capture.CharacterName.Equals(selected.Character, StringComparison.OrdinalIgnoreCase)
            || capture.Target is not { RawType: 0 or 1 } || !MyTargetSpells.Matches(capture))
            throw new InvalidDataException("Waiting for a verified NPC or player target spell list.");
        pickerCapture = capture;
        changingSource = true; SpellSourceChoice.SelectedIndex = 1; changingSource = false;
        resultsFromTarget = true;
        AddTargetChoice.SelectedIndex = capture.Target.RawType == 0 ? 1 : 0;
        RefreshSearchResults();
    }
    private void RefreshSearchResults()
    {
        string query = SpellSearch.Text.Trim();
        bool Match(int id, string name) => query.Length == 0 || id.ToString() == query || name.Contains(query, StringComparison.OrdinalIgnoreCase);
        int? selectedId = (SearchResults.SelectedItem as SpellSearchChoice)?.Id;
        CasterFilterPanel.Visibility = resultsFromTarget ? W.Visibility.Visible : W.Visibility.Collapsed;
        if (resultsFromTarget)
        {
            var capture = pickerCapture;
            SearchResults.ItemsSource = capture is null ? Array.Empty<SpellSearchChoice>() : capture.Buffs!.Effects
                .Where(s => FilterAll.IsChecked == true || !string.IsNullOrWhiteSpace(s.CasterName)
                    && (FilterOthers.IsChecked == true
                        ? !s.CasterName.Equals(capture.CharacterName, StringComparison.OrdinalIgnoreCase)
                        : s.CasterName.Equals(capture.CharacterName, StringComparison.OrdinalIgnoreCase)))
                // Filter individual observations before deduplicating a spell that multiple players cast.
                .Where(s => Match(s.SpellId, s.SpellName ?? $"Spell {s.SpellId}"))
                .DistinctBy(s => s.SpellId).Select(s => SearchChoice(s.SpellId, s.SpellName ?? $"Spell {s.SpellId}", s.IconId))
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            string filter = FilterAll.IsChecked == true ? "All casters (including unknown)" : FilterOthers.IsChecked == true ? "Others' casts" : "My casts";
            SearchHint.Text = capture is null ? "Read the current target to load its spells."
                : $"{SearchResults.Items.Count} spells · {capture.Target!.Name} · {filter} · use Read target to refresh";
        }
        else
        {
            SearchResults.ItemsSource = query.Length == 0 ? Array.Empty<SpellSearchChoice>() : spellNames.Where(p => Match(p.Key, p.Value))
                .OrderBy(p => p.Value, StringComparer.OrdinalIgnoreCase).Take(80)
                .Select(p => SearchChoice(p.Key, p.Value, spellSource?.Records?.GetValueOrDefault(p.Key)?.Icon)).ToArray();
            SearchHint.Text = query.Length == 0 ? "Search spell names or an ID. Any rank counts automatically." : "Up to 80 matches · choose a section, then Add spell";
        }
        SearchResults.SelectedItem = SearchResults.Items.OfType<SpellSearchChoice>().FirstOrDefault(c => c.Id == selectedId)
            ?? SearchResults.Items.OfType<SpellSearchChoice>().FirstOrDefault();
    }
}
