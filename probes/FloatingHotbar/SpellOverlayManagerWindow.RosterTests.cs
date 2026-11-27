namespace TeamBar;

public partial class SpellOverlayManagerWindow
{
    private async Task TestInputRosterRefresh(Action<bool, string> check)
    {
        var originalClients = ReadConnectedInputClients;
        var originalRefresh = RequestInputRosterRefresh;
        var originalRequest = RequestSpellInput;
        var originalDiscovery = DiscoverCharacters;
        var originalDetected = detected;
        var originalNextDiscovery = nextDiscovery;
        int refreshes = 0;
        var requests = new List<SpellClickRequest>();
        pollTimer.Stop();
        try
        {
            var connected = SpellDockTests.Clients();
            var extra = connected[0] with { ProcessId = 303, StartedUtcTicks = 3003, Window = 303,
                Character = connected[0].Character with { Name = "Returning fixture" } };
            var observed = connected.Append(extra).ToArray();
            ReadConnectedInputClients = () => connected;
            check(DockInputClients(observed).Select(c => c.ProcessId).SequenceEqual(connected.Select(c => c.ProcessId)),
                "dock recipient stamps exclude newly discovered or deselected characters until the host connects them");
            var replaced = connected[0] with { StartedUtcTicks = connected[0].StartedUtcTicks + 1 };
            check(!DockInputClients([replaced]).Any(), "a reused process cannot inherit a dock recipient's old connection");
            var manual = connected[0] with { UserAssigned = true, BuildSha256 = Identity.BuildHash };
            ReadConnectedInputClients = () => [manual];
            check(DockInputClients(observed).Length == 1 && DockInputClients([replaced]).Length == 0
                && DockInputClients([connected[0] with { Character = extra.Character }]).Length == 0,
                "spell data can confirm a manually assigned hot-key recipient by character, class, and live window");
            ReadConnectedInputClients = () => connected;
            var leveled = connected[0] with { Left = connected[0].Left + 10,
                Character = connected[0].Character with { Level = connected[0].Character.Level + 1 } };
            check(DockInputClients([leveled]).Length == 1 && SameDiscoveredRoster([connected[0]], [leveled]),
                "movement and level changes do not rebuild healthy character connections");
            ReadConnectedInputClients = () => observed;
            check(DockInputClients(observed).Length == observed.Length,
                "dock descriptions include a known returning character after the runtime confirms its connection");

            RequestInputRosterRefresh = () => refreshes++;
            DiscoverCharacters = () => new(originalDetected.Append(extra).ToArray(), []);
            await RefreshCharacters();
            check(refreshes == 1, "a changed discovery automatically asks the active input host to refresh its roster");
            await RefreshCharacters();
            check(refreshes == 1, "unchanged discovery does not repeatedly request a roster refresh");
            detected = originalDetected;

            string session = clickSession ?? "fixture";
            clickSession = session;
            nextDiscovery = DateTime.UtcNow.AddHours(1);
            RequestSpellInput = request =>
            {
                requests.Add(request);
                return request.Kind == "dock-tap"
                    ? Task.FromResult<SpellClickReply?>(new(false, "Characters changed · refreshing automatically", Session: session,
                        RefreshCharactersRequired: true)) : originalRequest(request);
            };
            var reply = await SendSpellClick(new("dock-tap", ButtonId: "fixture"));
            check(reply?.RefreshCharactersRequired == true && refreshes == 2 && nextDiscovery == DateTime.MinValue
                && clickRegistrations.Count == 0 && clickSession is null,
                "stale dock recipients trigger discovery and invalidate the old click registration automatically");
            await RefreshClickSession();
            check(requests.Count(r => r.Kind == "dock-tap") == 1 && requests.All(r => r.Kind != "tap"),
                "repairing a changed character roster never retries the rejected input gesture");
        }
        finally
        {
            ReadConnectedInputClients = originalClients; RequestInputRosterRefresh = originalRefresh;
            RequestSpellInput = originalRequest; DiscoverCharacters = originalDiscovery;
            detected = originalDetected; nextDiscovery = originalNextDiscovery;
            nextClickCheck = DateTime.MinValue;
            if (!closed) pollTimer.Start();
        }
    }
}
