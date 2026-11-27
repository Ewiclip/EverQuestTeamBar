using TeamBar;

internal sealed partial class FloatingWorkspace
{
    private (string Profile, TeamDefinition Team, SavedButton[] Buttons)? pendingConfiguration;
    internal void QueueConfiguration(string profile, TeamDefinition team, SavedButton[] buttons, bool preserveClearedHotkeys = false)
    {
        if (preserveClearedHotkeys && live.FloatingHotkeysCleared)
        {
            live = live.RememberOverlayRecipients(buttons);
            return;
        }
        live = live with { FloatingHotkeysCleared = false };
        pendingConfiguration = (profile, team, buttons);
        ApplyPendingConfiguration();
    }
    private void ApplyPendingConfiguration()
    {
        // A partial observation must never replace the original identity roster.
        if (pendingConfiguration is not { } update || !CanReconnect || identityBlocked || identityUnavailable) return;
        try
        {
            var team = TeamEditing.MergeDetected(update.Team, current);
            var bindings = TeamEditing.Bind(team, current);
            Routing.Validate(team, bindings);
            foreach (var button in update.Buttons) StandaloneButtons.Validate(button.Value);
            var setup = live with { Team = team, Clients = bindings, Identities = current };
            var next = BuildSurfaces(setup, update.Buttons, update.Profile, current);
            // Preserve explicit positions before rebuilding visible geometry. The
            // same mouse-guard HWND and native session stay in place throughout.
            foreach (var surface in surfaces) SavePosition(surface);
            surfaces.Clear(); surfaces.AddRange(next); live = setup; appliedButtons = update.Buttons;
            hovered = null; hoveredAction = -1;
            PrepareSurfaces();
            if (surfaces.Count > 0) Show(); else Hide();
            pendingConfiguration = null;
            Write("configuration_updated", new { buttons = appliedButtons.Length, groups = team.Groups?.Length ?? 0, inputSent = false });
        }
        catch (Exception error)
        {
            pendingConfiguration = null;
            Write("configuration_update_failed", new { error.Message });
            TeamBarApplication.Current?.NotifyInputRecovery("Saved changes could not be shown: " + error.Message);
        }
    }
}

internal sealed partial class AppInputSession
{
    internal static void UpdateSavedConfiguration(string profile, TeamDefinition team, SavedButton[] buttons, bool preserveClearedHotkeys = false)
    {
        string full = Path.GetFullPath(profile);
        if (recovery is { } watch && string.Equals(watch.Profile, full, StringComparison.OrdinalIgnoreCase))
            watch.UpdateConfiguration(team, buttons, preserveClearedHotkeys);
        if (Current is { IsClosed: false } active && string.Equals(active.Profile, full, StringComparison.OrdinalIgnoreCase))
            active.workspace.QueueConfiguration(full, team, buttons, preserveClearedHotkeys);
    }
}
