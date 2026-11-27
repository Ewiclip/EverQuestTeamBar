using System.Text.Json;
using W = System.Windows;
using C = System.Windows.Controls;

namespace TeamBar;

public partial class SetupWindow
{
    private bool settingFloating;
    private readonly W.Threading.DispatcherTimer floatingSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private void InitializeFloatingPreferences()
    {
        floatingSaveTimer.Tick += (_, _) => { floatingSaveTimer.Stop(); SaveFloatingBehavior(); };
        Closing += (_, _) => FlushFloatingBehavior();
        Closed += (_, _) => floatingSaveTimer.Stop();
        LoadFloatingPreferences();
    }
    private void LoadFloatingPreferences()
    {
        settingFloating = true;
        try
        {
            FloatingSize.Value = settings.Floating.Scale * 100; FloatingOpacity.Value = settings.Floating.Opacity * 100;
            FloatingLocked.IsChecked = settings.Floating.Locked; FloatingHideHeaders.IsChecked = settings.Floating.HideHeaders;
        }
        finally { settingFloating = false; }
        UpdateFloatingPreview(settings.Floating);
    }
    private enum PreferencePage { General, MemoryReading, Floating }
    private void ShowPreferencePage(PreferencePage page)
    {
        GeneralPreferencePanel.Visibility = page == PreferencePage.General ? W.Visibility.Visible : W.Visibility.Collapsed;
        MemoryPreferencePanel.Visibility = page == PreferencePage.MemoryReading ? W.Visibility.Visible : W.Visibility.Collapsed;
        FloatingPreferencePanel.Visibility = page == PreferencePage.Floating && GeneralSettingsView.Visibility == W.Visibility.Visible ? W.Visibility.Visible : W.Visibility.Collapsed;
        foreach (var (button, section) in new[] { (GeneralPreferencesNav, PreferencePage.General), (MemoryPreferencesNav, PreferencePage.MemoryReading), (FloatingPreferencesNav, PreferencePage.Floating) })
        {
            button.SetResourceReference(C.Control.BackgroundProperty, page == section ? "SelectedSurface" : "ButtonSurface");
            button.SetResourceReference(C.Control.BorderBrushProperty, page == section ? "SelectedBorder" : "Line");
        }
    }
    private void ShowFloatingPreferences(bool show) => ShowPreferencePage(show ? PreferencePage.Floating : PreferencePage.General);
    private void GeneralPreferences_Click(object sender, W.RoutedEventArgs e) => ShowFloatingPreferences(false);
    private void MemoryPreferences_Click(object sender, W.RoutedEventArgs e) => ShowPreferencePage(PreferencePage.MemoryReading);
    private void FloatingPreferences_Click(object sender, W.RoutedEventArgs e) => ShowFloatingPreferences(true);
    private bool PersistFloating(FloatingAppearance value)
    {
        try
        {
            settingsStore.SaveFloating(value, settings.Floating); settings = settingsStore.Load();
            SettingsState.Text = review ? "REVIEW COPY · Floating controls" : "Floating preferences saved";
            SettingsState.SetResourceReference(C.TextBlock.ForegroundProperty, "Success");
            SettingsFeedback.Text = review ? "This preview changes no saved settings and sends no game input." : "Applies to floating buttons and groups automatically.";
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            SettingsState.Text = "Could not save floating preferences"; SettingsState.SetResourceReference(C.TextBlock.ForegroundProperty, "Error"); SettingsFeedback.Text = error.Message;
            return false;
        }
    }
    private FloatingAppearance FloatingBehavior() => settings.Floating with { Scale = FloatingSize.Value / 100, Opacity = FloatingOpacity.Value / 100, Locked = FloatingLocked.IsChecked == true, HideHeaders = FloatingHideHeaders.IsChecked == true };
    private void FloatingValue_Changed(object sender, W.RoutedPropertyChangedEventArgs<double> e)
    {
        if (!initialized || settingFloating) return;
        UpdateFloatingPreview(FloatingBehavior()); floatingSaveTimer.Stop(); floatingSaveTimer.Start();
    }
    private void FloatingToggle_Changed(object sender, W.RoutedEventArgs e)
    { if (initialized && !settingFloating) { floatingSaveTimer.Stop(); SaveFloatingBehavior(); } }
    private void SaveFloatingBehavior()
    {
        var value = FloatingBehavior();
        if (!LocalUserSettingsStore.SameFloating(value, settings.Floating)) PersistFloating(value);
        LoadFloatingPreferences();
    }
    private void FlushFloatingBehavior() { if (floatingSaveTimer.IsEnabled) { floatingSaveTimer.Stop(); SaveFloatingBehavior(); } }
    private void UpdateFloatingPreview(FloatingAppearance appearance)
    {
        FloatingSizeText.Text = $"{appearance.Scale:P0}"; FloatingOpacityText.Text = $"{appearance.Opacity:P0}";
        using var bitmap = FloatingPainter.Preview(appearance with { ApplicationTheme = settings.Theme });
        using var bytes = new MemoryStream(); bitmap.Save(bytes, System.Drawing.Imaging.ImageFormat.Png); bytes.Position = 0;
        var source = new System.Windows.Media.Imaging.BitmapImage(); source.BeginInit(); source.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; source.StreamSource = bytes; source.EndInit(); source.Freeze(); FloatingPreview.Source = source;
        FloatingPreviewCaption.Text = $"{settings.Theme} appearance · follows Settings → General · preview sends no input";
    }
#if !TEAMBAR_PUBLIC_RELEASE
    private void FloatingSettingsSelfTest()
    {
        static void Check(bool value, string why) { if (!value) throw new InvalidOperationException(why); }
        string draft = JsonSerializer.Serialize(CurrentTeam());
        ShowGeneralSettings(true); ShowFloatingPreferences(true);
        var legacy = FloatingThemes.Presets[1] with { Id = "saved-custom", Name = "Saved custom", Text = "#AACCFF" };
        settingsStore.SaveFloating(settings.Floating with { ThemeId = legacy.Id, CustomThemes = [legacy] }, settings.Floating);
        ShowGeneralSettings(false); ShowGeneralSettings(true); ShowFloatingPreferences(true);
        FloatingSize.Value = 135; FloatingOpacity.Value = 65; FlushFloatingBehavior();
        FloatingLocked.IsChecked = true; FloatingHideHeaders.IsChecked = true;
        Check(settingsStore.Load().Floating is { Scale: 1.35, Opacity: .65, Locked: true, HideHeaders: true }, "Floating behavior controls failed to persist.");
        LightThemeChoice.IsChecked = true;
        Check(FloatingThemes.ForApplication(settings).Selected == FloatingThemes.ForAppTheme(AppearanceTheme.Light)
            && settings.Floating.ThemeId == legacy.Id && settings.Floating.CustomThemes.Single() == legacy,
            "App appearance must override rendering without discarding saved legacy palettes.");
        var working = settingsStore; settingsStore = new UserSettingsTests.FailingStore(settings);
        FloatingLocked.IsChecked = false;
        Check(FloatingLocked.IsChecked == true && settings.Floating.Locked, "Failed preference save did not restore checkbox."); settingsStore = working;
        settingsStore.SaveFloating(settings.Floating with { Scale = .9 }, settings.Floating);
        ShowGeneralSettings(false); ShowGeneralSettings(true);
        Check(settings.Floating.Scale == .9 && draft == JsonSerializer.Serialize(CurrentTeam()), "Settings reload lost the team draft or ignored a concurrent update.");
        ShowGeneralSettings(false);
    }
    private void ReviewFloatingThemes(string directory)
    {
        ShowPlacement(false); ShowGeneralSettings(true); DarkThemeChoice.IsChecked = true; ShowFloatingPreferences(true);
        Settle(); Export(Path.Combine(directory, "17-floating-settings.png"));
        LightThemeChoice.IsChecked = true; Settle(); Export(Path.Combine(directory, "19-floating-settings-light.png"));
        Width = 980; Height = 720; Settle(); Export(Path.Combine(directory, "20-floating-settings-compact.png"));
        Width = 1200; Height = 920; DarkThemeChoice.IsChecked = true;
        FloatingThemeTests.Export(directory);
    }
#endif
}
