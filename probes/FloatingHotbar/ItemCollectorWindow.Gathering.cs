namespace TeamBar;

public partial class ItemCollectorWindow
{
    private GatheringLogReader gatheringReader = new();
    private readonly Dictionary<string, GatheringObservation> gathering = [];
    private string? selectedGatheringLog;
    private DateTimeOffset nextLogSearch;
    private GatheringHistorySnapshot? gatheringSnapshot;
    private bool gatheringNeedsSharing;
    private void ResetGathering()
    {
        gatheringReader = new(); gathering.Clear(); gatheringSnapshot = null; gatheringNeedsSharing = false; selectedGatheringLog = null; nextLogSearch = default;
        GatheringList.ItemsSource = null;
        GatheringState.Text = "Waiting for a verified Live zone. Enable game logging with /log on to collect new successes.";
    }
    private void ChooseGatheringLog_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (review || ClientChoice.SelectedItem is not DetectedClient client) return;
        string installation = CollectorBuild.ForClient(client).InstallationPath;
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "EverQuest character log (eqlog_*.txt)|eqlog_*.txt", CheckFileExists = true,
            InitialDirectory = Path.Combine(installation, "Logs") };
        if (dialog.ShowDialog(DialogOwner) != true) return;
        if (!GatheringLogReader.MatchesClientLog(dialog.FileName, installation, client.Character.Name))
        { GatheringState.Text = "Choose this character's log from the selected game's Logs folder; other installations and characters cannot supply its sources."; return; }
        selectedGatheringLog = dialog.FileName; gatheringReader = new();
        GatheringState.Text = "Log selected. Watching starts at its current end on the next reading.";
    }
    private async Task ReadGathering(DetectedClient client, int version)
    {
        if (closed || paused || review || version != generation || !DataCollectionAccess.CanCollect) return;
        var zoneCapture = targetError is null ? targetCapture : null;
        if (client.ClientChannel != "Live" || zoneCapture is null || zoneCapture.CharacterName != client.Character.Name
            || zoneCapture.BuildSha256 != client.BuildSha256 || zoneCapture.ProcessId != client.ProcessId
            || zoneCapture.ProcessStartedUtcTicks != client.StartedUtcTicks || DateTimeOffset.UtcNow - zoneCapture.ObservedAtUtc > TimeSpan.FromSeconds(5))
        { gatheringReader = new(); GatheringState.Text = "Waiting for a current, verified Live zone before attributing gathering successes."; return; }
        try
        {
            if (selectedGatheringLog is null && DateTimeOffset.UtcNow >= nextLogSearch)
            {
                nextLogSearch = DateTimeOffset.UtcNow.AddSeconds(10);
                string folder = Path.Combine(CollectorBuild.ForClient(client).InstallationPath, "Logs");
                var logs = await Task.Run(() => Directory.Exists(folder) ? Directory.GetFiles(folder, "eqlog_" + client.Character.Name + "_*.txt") : []);
                if (version != generation || closed || paused || !DataCollectionAccess.CanCollect) return;
                if (logs.Length == 1) selectedGatheringLog = logs[0];
                else { GatheringState.Text = logs.Length > 1 ? "More than one server log matches. Choose this character's active log." : "No character log found. Enable /log on in the game, or choose its log."; return; }
            }
            if (selectedGatheringLog is null) return;
            var reader = gatheringReader; string path = selectedGatheringLog;
            var found = await Task.Run(() => reader.Poll(path, zoneCapture.Zone, DateTimeOffset.UtcNow));
            if (version != generation || closed || paused || !DataCollectionAccess.CanCollect || !ReferenceEquals(reader, gatheringReader)
                || targetError is not null || targetCapture?.Zone != zoneCapture.Zone) return;
            GatheringState.Text = reader.Status;
            var records = found.Select(row => ContributionSources.Gathering(row, CaptureSource(client.ClientChannel, client.BuildSha256, "live-gathering-log-1"))).ToArray();
            for (int i = 0; i < found.Length; i++) gathering[ContributionPayload.RecordKey(records[i])] = found[i];
            if (found.Length > 0)
            {
                foreach (var key in gathering.OrderByDescending(entry => entry.Value.SeenAtUtc).Skip(500).Select(entry => entry.Key).ToArray()) gathering.Remove(key);
                GatheringList.ItemsSource = gathering.Values.OrderByDescending(row => row.SeenAtUtc).ToArray();
                gatheringSnapshot = new(1, "gathering-session", client.ClientChannel, client.BuildSha256, "live-gathering-log-1", gathering.Values.ToArray());
                gatheringNeedsSharing = true;
                if (DataTabs.SelectedItem == GatheringTab) UpdateSummary();
            }
            if (gatheringNeedsSharing && gatheringSnapshot is { } saved)
            {
                gatheringNeedsSharing = false;
                var sharingSource = CaptureSource(saved.ClientChannel, saved.BuildSha256, saved.Decoder);
                await ContributeSources(saved.Observations.Select(row => ContributionSources.Gathering(row, sharingSource)).ToArray(), version);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { if (version == generation && !closed) { gatheringReader = new(); GatheringState.Text = "Gathering unavailable: " + error.Message; } }
    }
    private void AcceptGatheringSnapshot(GatheringHistorySnapshot snapshot)
    {
        if (snapshot.SchemaVersion != 1 || snapshot.SourceKind != "gathering-session" || snapshot.Observations is null || snapshot.Observations.Length > 500)
            throw new InvalidDataException("Unsupported gathering snapshot.");
        var source = CaptureSource(snapshot.ClientChannel, snapshot.BuildSha256, snapshot.Decoder);
        var records = snapshot.Observations.Select(row => ContributionSources.Gathering(row, source)).ToArray();
        gathering.Clear(); gatheringSnapshot = snapshot;
        for (int i = 0; i < records.Length; i++) gathering[ContributionPayload.RecordKey(records[i])] = snapshot.Observations[i];
        GatheringList.ItemsSource = gathering.Values.OrderByDescending(row => row.SeenAtUtc).ToArray();
        GatheringState.Text = "Saved source associations. No logs are being read and no data is uploaded.";
    }
}
