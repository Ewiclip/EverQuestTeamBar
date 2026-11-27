using W = System.Windows;
namespace TeamBar;

public partial class SpellOverlayManagerWindow
{
    private readonly W.Threading.DispatcherTimer autoSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool overlaysHidden;
    private void InitializeAutoSave() => autoSaveTimer.Tick += (_, _) => FlushAutoSave();
    private void ScheduleAutoSave()
    {
        if (review || closed) return;
        autoSaveTimer.Stop(); autoSaveTimer.Start();
    }
    internal bool FlushAutoSave()
    {
        autoSaveTimer.Stop();
        if (review || closed || !dirty) return true;
        try
        {
            var library = new SpellOverlayLibrary(3, definitions.ToArray());
            savedBytes = SpellOverlayStore.Save(path, library, savedBytes); savedLibrary = library; dirty = false;
            if (!overlaysHidden) ReconcileSaved();
            Status("All changes saved automatically" + (overlaysHidden ? " · overlays hidden" : ""));
            return true;
        }
        catch (Exception error)
        {
            Status("Changes not saved: " + error.Message + " Your last saved settings are still in use.", true);
            return false;
        }
    }
    // Keep existing HWNDs and input registrations. Typing an alias should not
    // close and recreate every overlay or restart the native input host.
    private void ReconcileSaved()
    {
        if (!GameDataAccess.IsEnabled(MemoryFeature.SpellBarData)) return;
        var next = savedLibrary.Overlays.Where(d => d.Enabled).ToArray();
        generation++;
        foreach (string id in floating.Keys.Where(id => !next.Any(d => d.Id == id)).ToArray())
        {
            floating[id].Close(); floating.Remove(id); clickRegistrations.Remove(id);
        }
        var appearance = FloatingThemes.ForApplication(preferences.Load());
        foreach (var definition in next)
            if (!floating.ContainsKey(definition.Id)) floating.Add(definition.Id, CreateFloatingOverlay(appearance.Selected));
        applied = next; UpdateOverlayControls(); nextClickCheck = DateTime.MinValue;
        RememberLoaded(applied.Length > 0);
        _ = Poll();
    }
    internal async void ButtonsChanged()
    {
        if (closed || review) return;
        await RefreshDockLibrary();
        if (!closed) { nextClickCheck = DateTime.MinValue; await Poll(); }
    }
}
