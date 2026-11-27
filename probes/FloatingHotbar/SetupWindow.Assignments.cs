using W = System.Windows;
namespace TeamBar;
public partial class SetupWindow
{
    private DetectedClient[] assignedClients = [];
    private GameViewWindow[] openWindowKeys = [];
    private readonly Func<GameSession[]> inputWindows;
    private void MonitorOpenWindows()
    {
        var timer = new W.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        bool polling = false;
        long nextIdentityAttempt = 0;
        timer.Tick += async (_, _) =>
        {
            if (!IsVisible || closed || applying || refreshing || polling) return;
            polling = true;
            try
            {
                var windows = await Task.Run(layoutWindows);
                var inputCandidates = layoutWindows == inputWindows ? windows : await Task.Run(inputWindows);
                if (closed || !IsVisible || applying || refreshing) return;
                var keys = inputCandidates.Select(GameViewWindow.From).OrderBy(w => w.ProcessId).ToArray();
                bool changed = !keys.SequenceEqual(openWindowKeys);
                openWindowKeys = keys;
                bool checkFullscreenNames = ScreensView.IsVisible && RefreshScreenWindowedWarning(windows) && layoutWindows == inputWindows;
                bool recheckIdentity = GameDataAccess.IsEnabled(MemoryFeature.ScreenDetection) &&
                    (checkFullscreenNames || keys.Any(k => !clients.Any(c => k.Matches(c.Client))));
                if (changed || (recheckIdentity && Environment.TickCount64 >= nextIdentityAttempt))
                {
                    nextIdentityAttempt = Environment.TickCount64 + 5000;
                    UpdateRosterCaption();
                    await RefreshClients(background: true);
                }
                if (inlineDraft is not null && ScreensView.IsVisible) RefreshInlineRoster(windows);
            }
            catch (Exception error) { if (!closed) Status(error.Message, true); }
            finally { polling = false; }
        };
        IsVisibleChanged += (_, _) => { if (IsVisible) timer.Start(); else timer.Stop(); };
        Closed += (_, _) => timer.Stop();
    }
    private void UpdateRosterCaption()
    {
        int unassigned = openWindowKeys.Count(k => !clients.Any(c => k.Matches(c.Client)));
        TeamCount.Text = $"{clients.Count(c => c.Included)} of {clients.Count} selected"
            + (unassigned > 0 ? $" · {unassigned} unassigned EQ window{(unassigned == 1 ? "" : "s")}" : "");
    }
    private void RestoreManualAssignments()
    {
        if (review) return;
        try { assignedClients = ManualAssignments.Load(profilePath); }
        catch (Exception error) { Status("Could not restore manual assignments: " + error.Message, true); }
        ApplyMemoryPreference();
    }
    private Discovery ScanClients()
    {
        var auto = GameDataAccess.IsEnabled(MemoryFeature.ScreenDetection) ? Identity.Discover(assignedClients.Select(c => c.ProcessId)) : new Discovery([], []);
        var found = auto.Clients.ToList(); var errors = auto.Errors.ToList();
        foreach (var client in assignedClients)
            try { found.Add(ClientSessions.Verify(client)); }
            catch (Exception e) { errors.Add(e.Message); }
        // An automatic discovery must not compete with an explicitly assigned character.
        var manualNames = assignedClients.Select(c => c.Character.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        found.RemoveAll(c => !c.UserAssigned && manualNames.Contains(c.Character.Name));
        return new(found.OrderBy(c => c.Left).ToArray(), errors.ToArray());
    }
    private async void AssignWindows_Click(object sender, W.RoutedEventArgs e)
    {
        if (refreshing || applying) return;
#if !TEAMBAR_PUBLIC_RELEASE
        if (review) { SampleAssignments().ShowDialog(); return; }
#endif
        try
        {
            var dialog = new WindowAssignments(this, CurrentTeam(), ClientSessions.ListWindows(), assignedClients);
            dialog.ShowDialog();
            if (dialog.Result is null) return;
            bool reconnect = string.Equals(AppInputSession.ActiveProfile, profilePath, StringComparison.OrdinalIgnoreCase);
            ManualAssignments.Save(profilePath, dialog.Result);
            if (reconnect) AppInputSession.Stop();
            assignedClients = dialog.Result; ApplyMemoryPreference(); await RefreshClients();
            if (reconnect && SelectedClients().Length > 0) await OpenBar();
        }
        catch (Exception error) { Status(error.Message, true); }
    }
#if !TEAMBAR_PUBLIC_RELEASE
    private WindowAssignments SampleAssignments()
    {
        var windows = new[] { new GameSession(11, 1, 111, 0, "Sample · DISPLAY1 · (0, 0) · 1920 × 1080"), new GameSession(22, 2, 222, 1920, "Sample · DISPLAY2 · (1920, 0) · 2560 × 1440") };
        return new(this, CurrentTeam(), windows, [], expected => ClientSessions.Verify(expected, pid => windows.Single(w => w.ProcessId == pid), _ => throw new InvalidOperationException("Manual assignment must not read character memory.")), () => windows);
    }
    private void AssignmentSelfTest()
    {
        var dialog = SampleAssignments();
        try
        {
            dialog.Show(); dialog.UpdateLayout();
            dialog.Select(0, "Runa", "Shadowknight"); dialog.Select(1, "Fellwort", "Shaman");
            bool rejected = false; try { dialog.BuildAssignments(); } catch (InvalidDataException) { rejected = true; }
            if (!rejected) throw new InvalidOperationException("Manual assignment did not require confirmation.");
            dialog.Confirm(); var assigned = dialog.BuildAssignments();
            if (assigned.Length != 2 || assigned.Any(c => !c.UserAssigned)) throw new InvalidOperationException("Manual assignment lost source.");
            dialog.Select(1, "Runa", "Cleric"); rejected = false;
            try { dialog.BuildAssignments(); } catch (InvalidDataException) { rejected = true; }
            if (!rejected) throw new InvalidOperationException("Duplicate manual character accepted.");
        }
        finally { dialog.Close(); }
    }
    private void ReviewAssignments(string destination)
    {
        DarkThemeChoice.IsChecked = true;
        var dialog = SampleAssignments();
        try
        {
            dialog.Show(); dialog.Select(0, team.Characters[0].Name, "Shadowknight"); dialog.Select(1, team.Characters[1].Name, "Berserker");
            Settle(); dialog.Export(Path.Combine(destination, "14-assign-windows-dark.png"));
            LightThemeChoice.IsChecked = true; Settle(); dialog.Export(Path.Combine(destination, "15-assign-windows-light.png"));
        }
        finally { dialog.Close(); }
    }
#endif
}
