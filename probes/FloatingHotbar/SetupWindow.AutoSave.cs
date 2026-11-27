using W = System.Windows;
using C = System.Windows.Controls;
namespace TeamBar;

public partial class SetupWindow
{
    private readonly W.Threading.DispatcherTimer autoSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool libraryChanged;
    private void InitializeAutoSave()
    {
        autoSaveTimer.Tick += (_, _) => FlushAutoSave();
        Closed += (_, _) => autoSaveTimer.Stop();
    }
    private void ScheduleAutoSave()
    {
        if (review || closed) return;
        autoSaveTimer.Stop(); autoSaveTimer.Start();
    }
    // Saves each complete button independently so an unfinished new button
    // cannot block changes to an existing one. Invalid drafts stay in the editor.
    internal bool FlushAutoSave()
    {
        autoSaveTimer.Stop();
        if (review || closed) return true;

        if (applying || refreshing) { ScheduleAutoSave(); return false; }
        var errors = new List<string>(); bool written = libraryChanged;

        foreach (var draft in standaloneDrafts.Where(d => d.Dirty))
        {
            try
            {
                ValidateStandaloneDraft(draft);
                var doc = DocumentFor(draft);
                draft.SavedBytes = StandaloneButtons.Save(StandaloneButtons.PathFor(profilePath, draft.Id), doc, draft.SavedBytes);
                draft.Document = doc; draft.Dirty = false; written = true;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { errors.Add(draft.ManagementName + ": " + e.Message); }
        }
        if (changed || written)
        {
            try { Save(includeClients: false); written = true; }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException) { errors.Add(e.Message); }
        }
        if (written)
        {
            try
            {
                // Read the last committed group definitions, never an incomplete draft.
                AppInputSession.UpdateSavedConfiguration(profilePath, TeamEditing.Load(profilePath), StandaloneButtons.Load(profilePath));
                libraryChanged = false;
                SpellOverlayManagerWindow.CurrentManager?.ButtonsChanged();
            }
            catch (Exception e) { errors.Add("Saved; live update pending: " + e.Message); }
        }
        UpdatePreview();
        SaveState.Text = errors.Count == 0 ? "All changes saved automatically" : "Incomplete changes not saved";
        SaveState.SetResourceReference(C.TextBlock.ForegroundProperty, errors.Count == 0 ? "Success" : "Unsaved");
        ReviewDraftControl.Visibility = errors.Count > 0 && (changed || standaloneDrafts.Any(d => d.Dirty)) ? W.Visibility.Visible : W.Visibility.Collapsed;
        Status(errors.Count == 0 ? "Changes save automatically. Connected buttons update when the current click finishes." : string.Join(" ", errors), errors.Count > 0);
        return errors.Count == 0;
    }
}

