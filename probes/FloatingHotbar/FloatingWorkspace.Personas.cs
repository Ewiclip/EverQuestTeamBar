using TeamBar;

internal sealed partial class FloatingWorkspace
{
    private void RebindPersonas(DetectedClient[] identities)
    {
        var team = TeamEditing.MergeDetected(live.Team, identities);
        var bindings = TeamEditing.Bind(team, identities);
        Routing.Validate(team, bindings);
        var next = live with { Team = team, Clients = bindings, Identities = identities };
        // Resolve the entire library before replacing anything. Conflicted
        // buttons become inactive together with the matching persona update.
        var updates = surfaces.Select(surface =>
        {
            if (surface.Group is not null) return (Surface: surface, Actions: surface.Actions, Taps: surface.Taps);
            if (surface.Condition is not { } standalone)
                return (Surface: surface, Actions: Routing.Display(next).Buttons,
                    Taps: team.Buttons.Select(b => Routing.Resolve(team, b.Id, bindings)).ToArray());
            var taps = StandaloneButtons.Resolve(standalone, identities);
            return (Surface: surface,
                Actions: new[] { StandaloneButtons.Display(standalone, identities) },
                Taps: new[] { taps });
        }).ToArray();
        live = next;
        foreach (var update in updates) { update.Surface.Actions = update.Actions; update.Surface.Taps = update.Taps; }
        bool resized = false;
        foreach (var surface in surfaces.Where(s => s.Group is not null))
            if (RefreshGroup(surface, identities)) { surface.Bounds = new(surface.Bounds.Location, SurfaceSize(surface)); resized = true; }
        if (resized) RebuildRegion();
        hovered = null; hoveredAction = -1;
        if (live.IdentityFeature == MemoryFeature.ScreenDetection) TeamBarApplication.Current?.ObservePersonas(identities);
    }
}
