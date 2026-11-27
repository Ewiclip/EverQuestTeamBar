using W = System.Windows;

namespace TeamBar;

public partial class SetupWindow
{
    private static string PriorInstallationFolder(string subfolder = "")
    {
        var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TeamBar-QA", BuildEdition.Id, subfolder);
        return Directory.Exists(legacy) ? legacy : "";
    }

    private bool CanImportProfiles()
    {
        if (review || !InstalledAppData.IsInstalledBuild
            || !profilePath.Equals(Path.Combine(InstalledAppData.ProfilesDirectory, "team.json"), StringComparison.OrdinalIgnoreCase))
        {
            SettingsState.Text = "Import is available in the installed app";
            SettingsFeedback.Text = "Developer and review sessions do not import into your installed Team Bar data.";
            return false;
        }
        if (applying || refreshing || !FlushAutoSave())
        {
            SettingsState.Text = "Finish your current changes first";
            SettingsFeedback.Text = "Save complete button changes before importing a library or preferences.";
            return false;
        }
        FlushFloatingBehavior();
        return true;
    }

    private async void ImportProfilesClick(object sender, W.RoutedEventArgs e)
    {
        if (!CanImportProfiles()) return;
        try
        {
            string expected = ProfileMigration.Fingerprint(InstalledAppData.ProfilesDirectory, rejectUnknown: false);
            var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Select the folder containing team.json and its buttons folder",
                InitialDirectory = PriorInstallationFolder("profiles"), Multiselect = false };
            if (picker.ShowDialog(this) != true) return;
            ProfileMigration.ValidateLibrary(picker.FolderName);
            if (W.MessageBox.Show(this,
                "Import saved profiles from:\n" + picker.FolderName + "\n\nThis replaces your current button library and placements. Your current library and the selected original will be backed up first. The original folder stays unchanged. Legacy repeat settings become one tap per click. Connected hot keys will close; reconnect them after reviewing the imported buttons.\n\nContinue?",
                "Import existing profiles", W.MessageBoxButton.YesNo, W.MessageBoxImage.Question, W.MessageBoxResult.No) != W.MessageBoxResult.Yes) return;
            applying = true; ReviewRoot.IsEnabled = false; autoSaveTimer.Stop();
            if (string.Equals(AppInputSession.ActiveProfile, profilePath, StringComparison.OrdinalIgnoreCase))
            {
                if (!await AppInputSession.Clear(profilePath)) throw new IOException("Could not close the current hot keys. The library was not replaced.");
                AppInputSession.Stop(); await Backend.WaitForNativeShutdown();
            }
            var result = ProfileMigration.ImportLibrary(picker.FolderName, InstalledAppData.ProfilesDirectory, InstalledAppData.BackupsDirectory, expected);
            ReloadImportedLibrary();
            await RefreshClients();
            SettingsState.Text = "Profiles imported";
            SettingsFeedback.Text = "Review your buttons before reconnecting. Recoverable copies are saved at " + result.BackupDirectory;
        }
        catch (Exception error) { SettingsState.Text = "Profiles were not imported"; SettingsFeedback.Text = error.Message; }
        finally { applying = false; ReviewRoot.IsEnabled = true; }
    }

    private void ReloadImportedLibrary()
    {
        loading = true;
        try
        {
            selected = null; selectedGroup = null;
            team = TeamEditing.Load(profilePath); savedBytes = File.ReadAllBytes(profilePath);
            drafts.Clear(); standaloneDrafts.Clear(); groupDrafts.Clear(); rows.Clear(); choices.Clear(); clients.Clear(); assignedClients = [];
            foreach (var button in team.Buttons) drafts.Add(new(button));
            foreach (var group in team.Groups ?? []) groupDrafts.Add(group);
            LoadStandaloneLibrary(); changed = libraryChanged = false;
            ButtonList.ItemsSource = IsStandalone ? standaloneDrafts : groupDrafts;
            ButtonList.SelectedIndex = -1;
            buttonSearch = groupSearch = ""; lastButtonId = lastGroupId = null;
            RestoreManualAssignments();
            SpellOverlayManagerWindow.CurrentManager?.ButtonsChanged();
        }
        finally { loading = false; }
        FilterLibrary(switchLibrary: true); UpdatePreview();
    }

    private async void ImportPreferencesClick(object sender, W.RoutedEventArgs e)
    {
        if (!CanImportProfiles()) return;
        try
        {
            string destination = LocalUserSettingsStore.DefaultPath;
            byte[]? expected = File.Exists(destination) ? File.ReadAllBytes(destination) : null;
            var picker = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select saved Team Bar preferences", Filter = "Team Bar preferences (user-settings.json)|user-settings.json",
                InitialDirectory = PriorInstallationFolder(), CheckFileExists = true, Multiselect = false
            };
            if (picker.ShowDialog(this) != true) return;
            ProfileMigration.ValidatePreferences(picker.FileName, "user-settings.json");
            if (W.MessageBox.Show(this,
                "Replace your app preferences with:\n" + picker.FileName + "\n\nThis includes appearance, memory-reading and Data Collection preferences. Your current preferences will be backed up first. Contribution credentials stay in their current scope.\n\nContinue?",
                "Import preferences", W.MessageBoxButton.YesNo, W.MessageBoxImage.Question, W.MessageBoxResult.No) != W.MessageBoxResult.Yes) return;
            string backup = ProfileMigration.ImportPreferences(picker.FileName, destination, InstalledAppData.BackupsDirectory, expected);
            settings = settingsStore.Load(); ApplyAppearance(); LoadFloatingPreferences();
            var features = Enum.GetValues<MemoryFeature>();
            foreach (var feature in features) GameDataAccess.SetEnabled(feature, settings.IsEnabled(feature));
            if (TeamBarApplication.Current is { } app)
                foreach (var feature in features) await app.MemoryPreferenceChanged(feature, settings.IsEnabled(feature));
            ApplyMemoryPreference();
            SettingsState.Text = "Preferences imported";
            SettingsFeedback.Text = "Your previous preferences are saved at " + backup;
        }
        catch (Exception error) { SettingsState.Text = "Preferences import could not finish"; SettingsFeedback.Text = error.Message; }
    }

    private async void ImportSpellOverlaysClick(object sender, W.RoutedEventArgs e)
    {
        if (!CanImportProfiles()) return;
        bool restoreLoaded = false, managerStopped = false;
        string? backup = null;
        try
        {
            var picker = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select saved Team Bar spell overlays", Filter = "Team Bar spell overlays (spell-overlays.json)|spell-overlays.json",
                InitialDirectory = PriorInstallationFolder(), CheckFileExists = true, Multiselect = false
            };
            if (picker.ShowDialog(this) != true) return;
            string destination = SpellOverlayStore.DefaultPath;
            if (Path.GetFullPath(picker.FileName).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Choose a separate source spell-overlays.json file.");
            ProfileMigration.ValidatePreferences(picker.FileName, "spell-overlays.json");
            if (W.MessageBox.Show(this,
                "Replace your saved spell bars with:\n" + picker.FileName + "\n\nThis includes watched spells, click mappings, docked groups, and placements. Current overlay changes will be saved, the overlay manager will close, and your current file will be backed up first. Previously loaded overlays will reopen if Spell Bar Data is enabled. No clicks are replayed.\n\nContinue?",
                "Import spell overlays", W.MessageBoxButton.YesNo, W.MessageBoxImage.Question, W.MessageBoxResult.No) != W.MessageBoxResult.Yes) return;
            applying = true; ReviewRoot.IsEnabled = false;
            if (SpellOverlayManagerWindow.CurrentManager is { } manager)
            {
                if (!manager.FlushAutoSave()) throw new IOException("Finish or correct your unsaved spell-overlay changes before importing.");
                restoreLoaded = settingsStore.Load().SpellOverlaysLoaded;
                await manager.ExitOverlays(preserveLoadedState: true);
                if (SpellOverlayManagerWindow.CurrentManager is not null) throw new IOException("The current spell-overlay manager did not close. No saved overlays were replaced.");
                managerStopped = true;
                await Backend.WaitForNativeShutdown();
            }
            else restoreLoaded = settingsStore.Load().SpellOverlaysLoaded;

            // Exclude another overlay manager while replacing its saved file.
            // Take the snapshot after the old manager has finished saving and
            // released its runtime lock, so it cannot later overwrite the import.
            backup = ProfileMigration.ImportSpellOverlays(picker.FileName, destination, InstalledAppData.BackupsDirectory);
            managerStopped = true;
            ShowGeneralSettings(true);
            SettingsState.Text = "Spell overlays imported";
            SettingsFeedback.Text = "Your previous spell overlays are saved at " + backup;
        }
        catch (Exception error)
        {
            ShowGeneralSettings(true);
            SettingsState.Text = backup is null ? "Spell overlays were not imported" : "Spell overlays imported; refresh needed";
            SettingsFeedback.Text = error.Message + (backup is null ? "" : " Backup: " + backup);
        }
        finally
        {
            applying = false; ReviewRoot.IsEnabled = true;
            if (managerStopped && restoreLoaded && GameDataAccess.IsEnabled(MemoryFeature.SpellBarData))
            {
                try { SpellOverlayManagerWindow.Open(restore: true); }
                catch (Exception error)
                {
                    SettingsState.Text = backup is null ? "Saved overlays could not reopen" : "Spell overlays imported; reopen them when ready";
                    SettingsFeedback.Text = error.Message + (backup is null ? "" : " Backup: " + backup);
                }
            }
        }
    }
}
