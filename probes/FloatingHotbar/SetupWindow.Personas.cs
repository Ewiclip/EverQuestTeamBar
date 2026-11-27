namespace TeamBar;

public partial class SetupWindow
{
    internal void InputRecoveryStatus(string message, string? profile = null, DetectedClient[]? identities = null)
    {
        if (closed || refreshing || applying || profile is not null && !string.Equals(profilePath, profile, StringComparison.OrdinalIgnoreCase)) return;
        if (identities is { Length: > 0 })
        {
            // Update live session metadata without replacing unsaved button rules
            // or the user's current team selections.
            CaptureRules();
            team = TeamEditing.MergeDetected(CurrentTeam(), identities);
            foreach (var actual in identities)
            {
                var matches = clients.Where(c => c.Name.Equals(actual.Character.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length == 1) matches[0].Observe(actual);
                else if (matches.Length == 0)
                {
                    var option = new ClientOption(actual, true);
                    option.PropertyChanged += (_, _) => UpdatePreview(); clients.Add(option);
                }
            }
            NoClients.Visibility = System.Windows.Visibility.Collapsed;
            LoadButton();
        }
        Status(message);
    }
    internal void ObservePersonas(DetectedClient[] identities)
    {
        if (closed || refreshing) return;
        var updates = clients.Select(option => (Option: option,
            Actual: identities.SingleOrDefault(actual => Identity.SameCharacterSession(option.Client, actual))))
            .Where(pair => pair.Actual is not null && pair.Option.Client.Character != pair.Actual.Character).ToArray();
        if (updates.Length == 0) return;
        // Keep selections and unsaved rules; only refresh observed identity and
        // add the new persona to available selectors in this editor's draft.
        team = TeamEditing.MergeDetected(CurrentTeam(), updates.Select(pair => pair.Actual!).ToArray());
        foreach (var update in updates) update.Option.Observe(update.Actual!);
        LoadButton();
    }

    private async Task VerifySelectedPersonas(DetectedClient[] expected)
    {
        var observed = await Task.Run(() => expected.Select(client =>
        {
            var actual = ClientSessions.Verify(client);
            if (!Identity.SameCharacterSession(client, actual))
                throw new InvalidDataException("A character or game session changed. Refresh your team and review the mappings.");
            return actual;
        }).ToArray());
        ObservePersonas(observed);
    }
}
