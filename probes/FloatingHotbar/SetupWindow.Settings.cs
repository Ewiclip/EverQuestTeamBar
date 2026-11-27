using System.Text.Json;
using W = System.Windows;
using C = System.Windows.Controls;

namespace TeamBar;

public partial class SetupWindow
{
    private IUserSettingsStore settingsStore = new MemoryUserSettingsStore();
    private UserSettings settings = new();
    private bool settingTheme;
    private bool settingsOpen, settingMemory;
    private bool FeatureEnabled(MemoryFeature feature) => review ? settings.IsEnabled(feature) : GameDataAccess.IsEnabled(feature);

    private void InitializePreferences(IUserSettingsStore? store)
    {
        settingsStore = store ?? new LocalUserSettingsStore(LocalUserSettingsStore.DefaultPath);
        try { settings = settingsStore.Load(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            SettingsState.Text = "Could not load preferences";
            SettingsState.SetResourceReference(C.TextBlock.ForegroundProperty, "Error");
            SettingsFeedback.Text = error.Message;
        }
        if (review)
        {
            settingsStore = new MemoryUserSettingsStore(settings);
            SettingsState.Text = "REVIEW COPY · Appearance preview";
            SettingsFeedback.Text = "You can try both themes here. Your saved preferences stay unchanged.";
        }
        ApplyAppearance();
        ApplyMemoryPreference();
        InitializeFloatingPreferences();
    }
    private void ApplyAppearance()
    {
        ThemePalette.Apply(Resources, settings.Theme);
        if (!review) TeamBarApplication.Current?.SetGameViewTheme(settings.Theme);
        spellEditor?.SetSettingsTheme(settings.Theme);
        dataEditor?.SetSettingsTheme(settings.Theme);
        settingTheme = true;
        try { DarkThemeChoice.IsChecked = settings.Theme == AppearanceTheme.Dark; LightThemeChoice.IsChecked = settings.Theme == AppearanceTheme.Light;
        }
        finally { settingTheme = false; }
        UpdateTitleBarTheme();
        if (initialized) UpdateFloatingPreview(settings.Floating);
    }
    private void UpdateTitleBarTheme()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == 0) return;
        int dark = settings.Theme == AppearanceTheme.Dark ? 1 : 0;
        DwmSetWindowAttribute(handle, 20, ref dark, 4);
    }
    private void ShowGeneralSettings(bool show)
    {
        FlushFloatingBehavior();
        if (show)
        {
            try { settings = settingsStore.Load(); ApplyAppearance(); LoadFloatingPreferences(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { SettingsState.Text = "Could not load preferences"; SettingsFeedback.Text = error.Message; }
            ApplyMemoryPreference();
        }
        settingsOpen = show;
        if (show)
        {
            FlushAutoSave(); spellEditor?.FlushAutoSave();
            HotKeysTab.IsChecked = SpellOverlaysTab.IsChecked = DataCollectionTab.IsChecked = ScreensTab.IsChecked = false;
            ReviewRoot.Visibility = W.Visibility.Visible;
            SpellSettingsHost.Visibility = DataSettingsHost.Visibility = ScreensView.Visibility = W.Visibility.Collapsed;
        }
        FloatingPreferencePanel.Visibility = W.Visibility.Collapsed;
        ShowFloatingPreferences(false);
        if (show) ShowPlacement(false);
        PlacementButton.Visibility = show ? W.Visibility.Collapsed : W.Visibility.Visible;
        GeneralSettingsView.Visibility = SettingsFooter.Visibility = show ? W.Visibility.Visible : W.Visibility.Collapsed;
        EditorBody.Visibility = EditorFooter.Visibility = WindowLayoutActions.Visibility = show ? W.Visibility.Collapsed : W.Visibility.Visible;
        SectionHeading.Text = show ? "Settings" : "Hot Keys";
        SectionDescription.Text = show ? "Preferences for the whole app. Changes save automatically." : "Create a button once. Use it on its own or in a group.";
        SettingsButton.SetResourceReference(C.Control.BackgroundProperty, show ? "SelectedSurface" : "ButtonSurface");
        if (show) GeneralPreferencesNav.Focus();
        else if (HotKeysTab.IsChecked != true && SpellOverlaysTab.IsChecked != true && DataCollectionTab.IsChecked != true && ScreensTab.IsChecked != true) ShowHotKeys();
    }
    private void Settings_Click(object sender, W.RoutedEventArgs e) => ShowGeneralSettings(GeneralSettingsView.Visibility != W.Visibility.Visible);
    internal void OpenAppSettings() => ShowGeneralSettings(true);
    internal void OpenMemorySettings()
    { ShowGeneralSettings(true); ShowPreferencePage(PreferencePage.MemoryReading); MemoryPreferencesNav.Focus(); }

    private void ApplyMemoryPreference()
    {
        bool detect = FeatureEnabled(MemoryFeature.ScreenDetection);
        bool spells = FeatureEnabled(MemoryFeature.SpellBarData);
        bool collect = FeatureEnabled(MemoryFeature.DataCollection);
        settingMemory = true;
        try
        {
            ScreenDetectionChoice.IsChecked = detect;
            SpellBarDataChoice.IsChecked = spells;
            DataCollectionChoice.IsChecked = collect;
        }
        finally { settingMemory = false; }
        MemoryModeLabel.Text = detect || spells || collect ? "Game-data reading on" : "Manual mode · memory reading off";
        SpellOverlaysTab.IsEnabled = spells;
        SpellOverlaysTab.Opacity = spells ? 1 : .42;
        SpellOverlaysTab.ToolTip = spells ? null : "Enable Spell Bar Data in Settings → Memory Reading.";
        DataCollectionTab.IsEnabled = collect;
        DataCollectionTab.Visibility = collect ? W.Visibility.Visible : W.Visibility.Collapsed;
        if (!collect) CloseDataCollectors();
        RefreshCharactersButton.IsEnabled = detect || assignedClients.Length > 0;
        RefreshCharactersButton.Opacity = RefreshCharactersButton.IsEnabled ? 1 : .42;
        RefreshCharactersButton.ToolTip = detect ? "Detect loaded characters" : "Check manually assigned EQ windows.";
        NoClients.Text = detect ? "No characters detected. Load your team and refresh, or assign windows yourself."
            : "Open EverQuest, then choose Assign characters to match each window to a name and class.";
        if ((!spells && SpellOverlaysTab.IsChecked == true) || (!collect && DataCollectionTab.IsChecked == true)) ShowHotKeys();
    }

    internal async Task RefreshMemoryPreference(MemoryFeature feature)
    {
        settings = settingsStore.Load();
        ApplyMemoryPreference();
        if (feature == MemoryFeature.ScreenDetection) await RefreshClients();
    }

    private async void MemoryFeature_Changed(object sender, W.RoutedEventArgs e)
    {
        if (!initialized || settingMemory || sender is not C.CheckBox { Tag: string name } choice ||
            !Enum.TryParse<MemoryFeature>(name, out var feature)) return;
        bool enabled = choice.IsChecked == true;
        choice.IsEnabled = false;
        try
        {
            // Revocation takes effect immediately even if the preferences file is unavailable.
            if (!review && !enabled) GameDataAccess.SetEnabled(feature, false);
            switch (feature)
            {
                case MemoryFeature.ScreenDetection: settingsStore.SaveScreenDetection(enabled); break;
                case MemoryFeature.SpellBarData: settingsStore.SaveSpellBarData(enabled); break;
                case MemoryFeature.DataCollection: settingsStore.SaveDataCollection(enabled); break;
            }
        }
        catch (Exception error)
        {
            if (!enabled && !review)
                try { await NotifyMemoryFeatureChanged(feature, false); }
                catch { /* The feature's reader gate is already closed. */ }
            ApplyMemoryPreference();
            SettingsState.Text = "Could not save preference";
            SettingsState.SetResourceReference(C.TextBlock.ForegroundProperty, "Error");
            SettingsFeedback.Text = error.Message + (!enabled && !review ? " This feature is off for this session. Check the saved preference before your next launch." : "");
            choice.IsEnabled = true;
            return;
        }

        try
        {
            if (review) settings = settingsStore.Load();
            else
            {
                GameDataAccess.SetEnabled(feature, enabled);
                await NotifyMemoryFeatureChanged(feature, enabled);
            }
            ApplyMemoryPreference();
            SettingsState.Text = "Preferences saved";
            SettingsState.SetResourceReference(C.TextBlock.ForegroundProperty, "Success");
            SettingsFeedback.Text = "";
        }
        catch (Exception error)
        {
            ApplyMemoryPreference();
            SettingsState.Text = "Preference saved. Could not refresh Team Bar.";
            SettingsState.SetResourceReference(C.TextBlock.ForegroundProperty, "Error");
            SettingsFeedback.Text = error.Message;
        }
        finally { choice.IsEnabled = true; }
    }

    private async Task NotifyMemoryFeatureChanged(MemoryFeature feature, bool enabled)
    {
        if (TeamBarApplication.Current is { } host) { await host.MemoryPreferenceChanged(feature, enabled); return; }
        if (!enabled)
        {
            AppInputSession.RevokeGameData(feature);
            if (feature == MemoryFeature.SpellBarData && (spellEditor ?? SpellOverlayManagerWindow.CurrentManager) is { } manager)
                await manager.SuspendMemoryReading();
            if (feature == MemoryFeature.DataCollection) CloseDataCollectors();
        }
        await RefreshMemoryPreference(feature);
    }

    private void CloseDataCollectors()
    { dataEditor?.Close(); itemCollector?.Close(); }
    private void GameTerms_Navigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://www.daybreakgames.com/terms-of-service") { UseShellExecute = true });
        e.Handled = true;
    }

    private void Theme_Changed(object sender, W.RoutedEventArgs e)
    {
        if (!initialized || settingTheme || sender is not C.RadioButton { Tag: string name } || !Enum.TryParse<AppearanceTheme>(name, out var theme) || theme == settings.Theme) return;
        try
        {
            var updated = settings with { Theme = theme };
            settingsStore.Save(updated); settings = settingsStore.Load(); ApplyAppearance();
            SettingsState.Text = review ? "REVIEW COPY · Appearance preview" : "Appearance saved";
            SettingsState.SetResourceReference(C.TextBlock.ForegroundProperty, "Success");
            SettingsFeedback.Text = review ? "Your saved preferences stay unchanged in this review window." : $"{theme} mode is remembered on this computer. No restart needed.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            ApplyAppearance();
            SettingsState.Text = "Could not save appearance";
            SettingsState.SetResourceReference(C.TextBlock.ForegroundProperty, "Error");
            SettingsFeedback.Text = error.Message;
        }
    }
}

