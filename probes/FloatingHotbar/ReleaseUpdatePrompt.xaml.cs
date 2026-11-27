using W = System.Windows;
using Input = System.Windows.Input;

namespace TeamBar;

internal partial class ReleaseUpdatePrompt : W.Window
{
    private static ReleaseUpdatePrompt? open;
    private readonly ReleaseUpdateCoordinator updates;
    private readonly string version;
    private bool installing;

    private ReleaseUpdatePrompt(ReleaseUpdateCoordinator updates, string version, SetupWindow? settings)
    {
        this.updates = updates;
        this.version = version;
        InitializeComponent();
        Icon = AppBranding.WindowIcon;
        if (settings is null) ThemePalette.Apply(Resources, AppearanceTheme.Dark);
        else Resources.MergedDictionaries.Add(settings.Resources);
        if (settings is { IsVisible: true } && settings.WindowState != W.WindowState.Minimized)
        {
            Owner = settings;
            ShowInTaskbar = false;
            ShowActivated = settings.IsActive;
            WindowStartupLocation = W.WindowStartupLocation.CenterOwner;
        }
        VersionHeading.Text = $"Team Bar {version} is available";
        StatusText.Text = updates.RequiresEverQuestClosed
            ? "Close EverQuest before installing this update. Setup will close Team Bar."
            : "Setup will close Team Bar to complete the update.";
        updates.Changed += Refresh;
        Closed += (_, _) =>
        {
            updates.Changed -= Refresh;
            if (installing) updates.CancelDownload();
            if (ReferenceEquals(open, this)) open = null;
        };
    }

    internal static void Show(string version) => Show(ReleaseUpdateCoordinator.Shared, version);

    private static void Show(ReleaseUpdateCoordinator updates, string version)
    {
        var app = W.Application.Current;
        if (app is null || app.Dispatcher.HasShutdownStarted || updates.AvailableVersion != version ||
            updates.IsVersionSkipped(version) || updates.IsQueued || open?.version == version) return;
        open?.Close();
        var windows = app.Windows.OfType<SetupWindow>().ToArray();
        var settings = windows.FirstOrDefault(w => w.IsActive) ?? windows.FirstOrDefault(w => w.IsVisible) ?? windows.FirstOrDefault();
        var prompt = new ReleaseUpdatePrompt(updates, version, settings);
        open = prompt;
        prompt.Show();
    }

    private void Refresh()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.InvokeAsync(Refresh); return; }
        if (!ReferenceEquals(open, this)) return;
        if (updates.IsVersionSkipped(version) || updates.AvailableVersion != version || updates.IsQueued)
        {
            Close();
            return;
        }
        InstallButton.IsEnabled = SkipButton.IsEnabled = !updates.Busy;
        LaterButton.Content = installing ? "Cancel download" : "Later";
        if (installing) StatusText.Text = updates.Status;
    }

    private async void Install_Click(object sender, W.RoutedEventArgs e)
    {
        if (updates.Busy || installing) return;
        installing = true;
        try
        {
            if (await updates.InstallNow(version) && IsVisible) Close();
        }
        finally
        {
            installing = false;
            if (IsVisible) { Refresh(); StatusText.Text = updates.Status; }
        }
    }

    private void Skip_Click(object sender, W.RoutedEventArgs e)
    {
        try { updates.SkipVersion(version); }
        catch (Exception error) { StatusText.Text = "Could not save your choice. " + error.Message; }
    }

    private void Later_Click(object sender, W.RoutedEventArgs e) => Close();
    private void Prompt_KeyDown(object sender, Input.KeyEventArgs e)
    {
        if (e.Key == Input.Key.Escape) { e.Handled = true; Close(); }
    }
}
