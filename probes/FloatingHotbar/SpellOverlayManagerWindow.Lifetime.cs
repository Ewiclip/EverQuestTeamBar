using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using W = System.Windows;
using F = System.Windows.Forms;
namespace TeamBar;

public partial class SpellOverlayManagerWindow
{
    private bool exiting;
    private EventWaitHandle? activateSettings;
    private readonly System.Windows.Threading.DispatcherTimer activationTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private F.NotifyIcon? tray;
    private F.ContextMenuStrip? trayMenu;

    private static string ActivationName(string profile) => "Local\\TeamBar.SpellOverlaySettings."
        + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(profile).ToUpperInvariant())));
    internal static bool TryActivateExisting(string? storePath = null)
    {
        if (!EventWaitHandle.TryOpenExisting(ActivationName(storePath ?? SpellOverlayStore.DefaultPath), out var signal)) return false;
        using (signal) signal.Set();
        return true;
    }
    private void InitializeLifetime()
    {
        try
        {
            activateSettings = new(false, EventResetMode.AutoReset, ActivationName(path));
            activationTimer.Tick += (_, _) => { if (activateSettings?.WaitOne(0) == true) ReopenSettings(); };
            activationTimer.Start();
            if (TeamBarApplication.Current is null)
            {
                trayMenu = new();
                trayMenu.Items.Add("Open spell overlay settings", null, (_, _) => ReopenSettings());
                trayMenu.Items.Add("Show saved overlays", null, (_, _) => Act(ApplySaved));
                trayMenu.Items.Add("Hide overlays", null, (_, _) => Hide_Click(this, new()));
                trayMenu.Items.Add(new F.ToolStripSeparator());
                trayMenu.Items.Add("Exit spell overlays", null, async (_, _) => await ExitOverlays());
                tray = new() { Icon = AppBranding.TrayIcon, Text = "Team Bar · Spell overlays", ContextMenuStrip = trayMenu, Visible = true };
                tray.DoubleClick += (_, _) => ReopenSettings();
            }
            current = this;
        }
        catch { ReleaseLifetime(); runtimeLock?.Dispose(); throw; }
    }
    private void ReleaseLifetime()
    {
        activationTimer.Stop(); activateSettings?.Dispose(); activateSettings = null;
        tray?.Dispose(); tray = null; trayMenu?.Dispose(); trayMenu = null;
    }
    private void ReopenSettings()
    {
        if (closed || exiting) return;
        if (settingsOwner is { } owner)
        { owner.ShowSpellSettings(this); owner.Show(); owner.WindowState = W.WindowState.Normal; owner.Activate(); return; }
        if (TeamBarApplication.Current is { } app) { app.ShowSpells(); return; }
        Show(); WindowState = W.WindowState.Normal; Activate();
    }
    private bool ConfirmDiscardChanges() => W.MessageBox.Show((W.Window?)settingsOwner ?? this,
        "Discard your unsaved overlay changes? Your saved mappings will remain unchanged.", "Unsaved overlay changes",
        W.MessageBoxButton.YesNo, W.MessageBoxImage.Question) == W.MessageBoxResult.Yes;
    internal bool PrepareSettingsExit()
    {
        FlushAutoSave();
        if (!dirty) return true;
        if (!ConfirmDiscardChanges()) return false;
        RestoreSavedDraft(); return true;
    }
    private void RestoreSavedDraft()
    {
        string? selectedId = selected?.Id;
        loading = true;
        definitions.Clear();
        foreach (var definition in savedLibrary.Overlays) definitions.Add(definition);
        selected = definitions.FirstOrDefault(d => d.Id == selectedId) ?? definitions.FirstOrDefault();
        OverlayList.SelectedItem = selected;
        loading = false; dirty = false; lookupGeneration++; LoadEditor();
    }
    private void HandleSettingsClosing(CancelEventArgs e, bool persistent, Func<bool> discard)
    {
        if (!persistent || exiting) return;
        e.Cancel = true;
        if (!review) { FlushAutoSave(); Hide(); return; }
        if (dirty && !discard()) return;
        if (dirty) RestoreSavedDraft();
        Hide(); // The dispatcher, readings, click registration, and file lock remain alive.
    }
    internal async Task ExitOverlays(bool preserveLoadedState = false)
    {
        if (exiting) return;
        FlushAutoSave();
        if (dirty)
        {
            ReopenSettings();
            if (!ConfirmDiscardChanges()) return;
            RestoreSavedDraft();
        }
        try { if (!preserveLoadedState) RememberLoaded(false); }
        catch (Exception error) { Status("Could not save overlay startup state: " + error.Message, true); return; }
        exiting = true; pollTimer.Stop(); StopOverlays();
        try { await CloseOwnedClickHost(); }
        catch (Exception error) { Status("Could not close the spell input session: " + error.Message, true); }
        Close();
    }
    internal async Task SuspendMemoryReading()
    {
        // Keep drafts and the saved restoration preference; disabling access is
        // not an instruction to discard configuration or hide bars permanently.
        pollTimer.Stop(); StopOverlays(); detected = []; runtimeInitialized = false;
        lookupGeneration++; pickerCapture = null;
        if (resultsFromTarget) RefreshSearchResults();
        await CloseOwnedClickHost();
        Status(GameDataAccess.DisabledReason(MemoryFeature.SpellBarData));
    }
    private async void Exit_Click(object sender, W.RoutedEventArgs e) => await ExitOverlays();
}
