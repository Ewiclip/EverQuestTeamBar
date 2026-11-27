namespace TeamBar;

public partial class SpellOverlayManagerWindow
{
    internal Func<DetectedClient[]?> ReadConnectedInputClients = () => AppInputSession.Current?.ConnectedIdentities;
    internal Action RequestInputRosterRefresh = () => AppInputSession.Current?.RequestRosterRefresh();

    private DetectedClient[] DockInputClients(DetectedClient[] observed)
    {
        var connected = ReadConnectedInputClients();
        if (connected is null) return observed;
        // Discovery can see a newly logged-in character before the native host
        // has connected it. Advertise only the roster the host can actually use.
        // Deliberately deselected clients must never enter a dock request.
        return observed.Where(client => connected.Any(active => active.UserAssigned
            ? active.ProcessId == client.ProcessId && active.StartedUtcTicks == client.StartedUtcTicks && active.Window == client.Window
                && active.ClientChannel == client.ClientChannel && active.Character.ClassId == client.Character.ClassId
                && active.Character.Name.Equals(client.Character.Name, StringComparison.OrdinalIgnoreCase)
            : !client.UserAssigned && Identity.Same(active, client))).ToArray();
    }

    private static bool SameDiscoveredRoster(DetectedClient[] before, DetectedClient[] after) =>
        before.Length == after.Length && before.All(client => after.Count(other =>
            client.UserAssigned == other.UserAssigned && Identity.Same(client, other)) == 1);

    private void RefreshChangedCharacters()
    {
        nextDiscovery = DateTime.MinValue;
        nextClickCheck = DateTime.MinValue;
        RequestInputRosterRefresh();
    }
}
