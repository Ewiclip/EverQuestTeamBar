using W = System.Windows;
namespace TeamBar;

public partial class SetupWindow
{
    private SpellOverlayManagerWindow? spellEditor;
    private ItemCollectorWindow? dataEditor;
    private bool closingApplication;
    internal void ShowHotKeys() { HotKeysTab.IsChecked = true; SetSettingsTab(); }
    internal void ShowSpellSettings(SpellOverlayManagerWindow manager)
    {
        if (!FeatureEnabled(MemoryFeature.SpellBarData)) { OpenMemorySettings(); return; }
        if (!ReferenceEquals(spellEditor, manager))
        {
            spellEditor = manager;
            SpellSettingsHost.Content = manager.TakeSettingsContent(this);
            manager.Closed += (_, _) =>
            {
                if (ReferenceEquals(spellEditor, manager))
                { spellEditor = null; SpellSettingsHost.Content = null; if (SpellOverlaysTab.IsChecked == true) ShowHotKeys(); }
            };
        }
        manager.SetSettingsTheme(settings.Theme);
        SpellOverlaysTab.IsChecked = true;
        SetSettingsTab();
    }
    internal void ShowDataSettings(ItemCollectorWindow collector)
    {
        if (!FeatureEnabled(MemoryFeature.DataCollection)) { collector.Close(); OpenMemorySettings(); return; }
        if (!ReferenceEquals(dataEditor, collector))
        {
            dataEditor = collector;
            DataSettingsHost.Content = collector.TakeSettingsContent(this);
            collector.Closed += (_, _) =>
            {
                if (ReferenceEquals(dataEditor, collector))
                { dataEditor = null; DataSettingsHost.Content = null; if (DataCollectionTab.IsChecked == true) ShowHotKeys(); }
            };
        }
        collector.SetSettingsTheme(settings.Theme);
        DataCollectionTab.IsChecked = true; SetSettingsTab();
    }
    private void SettingsTab_Changed(object sender, W.RoutedEventArgs e)
    {
        if (!initialized) return;
        if (DataCollectionTab.IsChecked == true && !FeatureEnabled(MemoryFeature.DataCollection) ||
            SpellOverlaysTab.IsChecked == true && !FeatureEnabled(MemoryFeature.SpellBarData))
        { OpenMemorySettings(); return; }
        if (DataCollectionTab.IsChecked == true && dataEditor is null)
        {
            if (TeamBarApplication.Current is { } host) host.ShowData();
            else if (review) ShowDataSettings(new ItemCollectorWindow(reviewOnly: true));
        }
        if (SpellOverlaysTab.IsChecked == true && spellEditor is null)
        {
            if (TeamBarApplication.Current is { } app) app.ShowSpells();
            else if (review) ShowSpellSettings(new SpellOverlayManagerWindow(reviewOnly: true));
        }
        SetSettingsTab();
    }
    private void SetSettingsTab()
    {
        if (!initialized) return;
        if (settingsOpen) ShowGeneralSettings(false);
        bool spells = SpellOverlaysTab.IsChecked == true;
        bool data = DataCollectionTab.IsChecked == true;
        bool screens = ScreensTab.IsChecked == true;
        ReviewRoot.Visibility = spells || data || screens ? W.Visibility.Collapsed : W.Visibility.Visible;
        ScreensView.Visibility = screens ? W.Visibility.Visible : W.Visibility.Collapsed;
        SpellSettingsHost.Visibility = spells ? W.Visibility.Visible : W.Visibility.Collapsed;
        DataSettingsHost.Visibility = data ? W.Visibility.Visible : W.Visibility.Collapsed;
        if (screens || spells) FlushAutoSave();
        if (spells) spellEditor?.SetSettingsTheme(settings.Theme);
        else spellEditor?.FlushAutoSave();
    }
    internal void CloseForExit() { closingApplication = true; Close(); closingApplication = false; }
}

public partial class SpellOverlayManagerWindow
{
    private W.FrameworkElement? settingsContent;
    private SetupWindow? settingsOwner;
    internal static SpellOverlayManagerWindow EnsureCurrent() => current ?? new SpellOverlayManagerWindow();
    internal W.FrameworkElement TakeSettingsContent(SetupWindow owner)
    {
        settingsOwner = owner;
        if (settingsContent is null)
        {
            settingsContent = (W.FrameworkElement)Content;
            Content = null;
            settingsContent.Resources = Resources;
            System.Windows.Controls.TextBlock.SetFontSize(settingsContent, 13);
            settingsContent.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Text");
        }
        _ = InitializeRuntime();
        return settingsContent;
    }
    internal void SetSettingsTheme(AppearanceTheme theme) => ThemePalette.Apply(Resources, theme);
}
