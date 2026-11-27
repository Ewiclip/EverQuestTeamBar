using W = System.Windows;

namespace TeamBar;

public partial class SetupWindow
{
    private bool updatesLoaded, changingUpdateChoice;

    private async void Updates_Loaded(object sender, W.RoutedEventArgs e)
    {
        if (updatesLoaded) return;
        updatesLoaded = true;
        UpdateInstalledVersion.Text = "Installed version: " + ReleaseUpdateConfiguration.InstalledVersion;
        if (review)
        {
            UpdateStatus.Text = "Update checks and installation are unavailable in design review.";
            CheckUpdatesButton.IsEnabled = InstallUpdateButton.IsEnabled = AutoCheckUpdates.IsEnabled = false;
            return;
        }
        var updates = ReleaseUpdateCoordinator.Shared;
        updates.Changed += RefreshUpdates;
        Closed += (_, _) => updates.Changed -= RefreshUpdates;
        RefreshUpdates();
        await updates.Check(manual: false);
    }

    private void RefreshUpdates()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.InvokeAsync(RefreshUpdates); return; }
        var updates = ReleaseUpdateCoordinator.Shared;
        UpdateChannel.Text = "Release channel: " + updates.Channel;
        UpdateStatus.Text = updates.Status;
        UpdateReleaseNotes.Text = updates.Available?.ReleaseNotes ?? "";
        UpdateRequirements.Text = updates.Available is null ? ""
            : updates.RequiresEverQuestClosed ? "This release changes native components. EverQuest must be closed before installation."
            : "This release keeps the existing native bridge. EverQuest can stay open while Team Bar updates.";
        changingUpdateChoice = true;
        try { AutoCheckUpdates.IsChecked = updates.AutoCheck; }
        finally { changingUpdateChoice = false; }
        AutoCheckUpdates.IsEnabled = updates.IsConfigured && !updates.Busy;
        CheckUpdatesButton.IsEnabled = updates.IsConfigured && !updates.Busy && !updates.IsQueued;
        InstallUpdateButton.IsEnabled = updates.Available is not null && !updates.Busy && !updates.IsQueued && TeamBarApplication.Current is not null;
        CancelUpdateButton.Visibility = updates.IsQueued ? W.Visibility.Visible : W.Visibility.Collapsed;
        CancelUpdateButton.IsEnabled = !updates.Busy;
    }

    private async void CheckUpdates_Click(object sender, W.RoutedEventArgs e)
    { if (!review) await ReleaseUpdateCoordinator.Shared.Check(manual: true); }
    private async void InstallUpdate_Click(object sender, W.RoutedEventArgs e)
    { if (!review && TeamBarApplication.Current is not null) await ReleaseUpdateCoordinator.Shared.QueueForExit(); }
    private void CancelUpdate_Click(object sender, W.RoutedEventArgs e)
    { if (!review) ReleaseUpdateCoordinator.Shared.CancelQueued(); }
    private void AutoCheckUpdates_Changed(object sender, W.RoutedEventArgs e)
    {
        if (!updatesLoaded || changingUpdateChoice || review) return;
        try { ReleaseUpdateCoordinator.Shared.SetAutoCheck(AutoCheckUpdates.IsChecked == true); }
        catch (Exception error) { RefreshUpdates(); UpdateStatus.Text = "Could not save update preference: " + error.Message; }
    }
}
