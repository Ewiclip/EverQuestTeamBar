using W = System.Windows;

namespace TeamBar;

public partial class SetupWindow
{
    private bool updatesLoaded, changingUpdateChoice;

    private void Updates_Loaded(object sender, W.RoutedEventArgs e)
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
        string? version = updates.AvailableVersion;
        bool hasOffer = version is not null && !updates.IsQueued;
        InstallUpdateButton.Visibility = hasOffer ? W.Visibility.Visible : W.Visibility.Collapsed;
        SkipUpdateButton.Visibility = hasOffer && !updates.IsVersionSkipped(version!) ? W.Visibility.Visible : W.Visibility.Collapsed;
        QueueUpdateButton.Visibility = hasOffer && updates.Available is not null ? W.Visibility.Visible : W.Visibility.Collapsed;
        UpdateRequirements.Visibility = UpdateRequirements.Text.Length > 0 ? W.Visibility.Visible : W.Visibility.Collapsed;
        UpdateReleaseNotes.Visibility = UpdateReleaseNotes.Text.Length > 0 ? W.Visibility.Visible : W.Visibility.Collapsed;
        InstallUpdateButton.IsEnabled = SkipUpdateButton.IsEnabled = QueueUpdateButton.IsEnabled = !updates.Busy;
        CancelUpdateButton.Visibility = updates.IsQueued ? W.Visibility.Visible : W.Visibility.Collapsed;
        CancelUpdateButton.IsEnabled = !updates.Busy;
    }

    private async void CheckUpdates_Click(object sender, W.RoutedEventArgs e)
    { if (!review) await ReleaseUpdateCoordinator.Shared.Check(manual: true); }
    private async void InstallUpdate_Click(object sender, W.RoutedEventArgs e)
    {
        var updates = ReleaseUpdateCoordinator.Shared;
        if (!review && updates.AvailableVersion is { } version) await updates.InstallNow(version);
    }
    private void SkipUpdate_Click(object sender, W.RoutedEventArgs e)
    {
        var updates = ReleaseUpdateCoordinator.Shared;
        if (review || updates.AvailableVersion is not { } version) return;
        try { updates.SkipVersion(version); }
        catch (Exception error) { UpdateStatus.Text = "Could not save your choice. " + error.Message; }
    }
    private async void QueueUpdate_Click(object sender, W.RoutedEventArgs e)
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
