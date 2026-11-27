// Discovery can temporarily lose a character during zoning. Retain only a
// recent positive identification for an already occupied, still-open window.
internal sealed class GameViewRosterCache(GameViewLayout layout, Func<long>? clockMilliseconds = null)
{
    internal const int GraceMilliseconds = 10_000;
    private readonly (GameViewWindow Key, long ConfirmedAt)?[] verified = new (GameViewWindow, long)?[layout.Characters.Length];

    internal GameSession?[] ResolveOpenWindows(GameSession[] windows, Func<Discovery> discover, IReadOnlyList<GameSession?> previous)
    {
        // Manual assignments are exact process/window instances. An identity
        // scan can only help if there is both a free window and a name to match.
        var pinned = layout.Resolve(windows, []);
        if (windows.Length > pinned.Count(seat => seat is not null) &&
            layout.Characters.Where((name, i) => pinned[i] is null && !string.IsNullOrWhiteSpace(name)).Any())
            return Resolve(windows, discover(), previous);
        Array.Clear(verified);
        return pinned;
    }

    internal GameSession?[] Resolve(GameSession[] windows, Discovery discovery, IReadOnlyList<GameSession?> previous)
    {
        var resolved = layout.Resolve(windows, discovery.Clients, previous);
        var occupied = resolved.OfType<GameSession>().Select(GameViewWindow.From).ToHashSet();
        long now = clockMilliseconds?.Invoke() ?? Environment.TickCount64;
        for (int i = 0; i < resolved.Length; i++)
        {
            string name = layout.Characters[i];
            bool SameName(DetectedClient c) => name.Length > 0 && string.Equals(name, c.Character.Name, StringComparison.OrdinalIgnoreCase);
            if (resolved[i] is { } found)
            {
                var key = GameViewWindow.From(found);
                verified[i] = discovery.Clients.Any(c => key.Matches(c) && SameName(c)) ? (key, now) : null;
                continue;
            }
            if (discovery.Errors.Length != 0 && verified[i] is { } last && now - last.ConfirmedAt < GraceMilliseconds &&
                previous.Count == resolved.Length && previous[i] is { } prior && GameViewWindow.From(prior) == last.Key &&
                !discovery.Clients.Any(c => last.Key.Matches(c) || SameName(c)) &&
                windows.FirstOrDefault(w => GameViewWindow.From(w) == last.Key) is { } live && occupied.Add(last.Key))
            {
                resolved[i] = live;
                continue;
            }
            verified[i] = null;
        }
        return resolved;
    }
}
