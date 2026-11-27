using System.Net.Http;

namespace TeamBar;

internal sealed record ContributionDestination(string Id, string Name, ContributionClient Client);
internal sealed record ContributionRoute(ContributionDestination Destination, ContributionSettings Settings);

internal sealed class ContributionDestinations : IDisposable
{
    internal ContributionDestination[] All { get; }
    internal Task Ready { get; }
    internal Task Completion => Task.WhenAll(All.Select(d => d.Client.Completion));
    internal event Action? Changed;

    internal ContributionDestinations(string? directory = null, Func<string, HttpMessageHandler>? handlers = null)
    {
        string root = directory ?? ContributionStorage.DefaultDirectory;
        ContributionClient Create(string id, string path) => new(path, handlers?.Invoke(id) ?? ContributionClient.CreateHandler());
        // Keep DKP's original path, credential protection and queue history intact.
        var dkp = Create("dkp", root);
        try { All = [new("eqtradeskills", "EQTradeskills", Create("eqtradeskills", Path.Combine(root, "EQTradeskills"))), new("dkp", "Generic API", dkp)]; }
        catch { dkp.Dispose(); throw; }
        foreach (var destination in All) destination.Client.Changed += Notify;
        Ready = Task.WhenAll(All.Select(d => d.Client.Ready));
    }

    private void Notify() => Changed?.Invoke();
    internal ContributionRoute[] Snapshot() => All.Where(d => d.Client.Settings is { Enabled: true, HasApiKey: true })
        .Select(d => new ContributionRoute(d, d.Client.Settings)).ToArray();
    internal bool IsCurrent(ContributionRoute route) => All.Contains(route.Destination)
        && route.Settings.Enabled && route.Settings.HasApiKey && ReferenceEquals(route.Settings, route.Destination.Client.Settings);

    internal async Task<string[]> Enqueue(ContributionRecord[] records, ContributionRoute[] routes, CancellationToken token)
    {
        // One full queue or damaged destination must not prevent another from receiving the same observation.
        var failures = await Task.WhenAll(routes.Select(async route =>
        {
            if (!IsCurrent(route)) return null;
            try
            {
                var compatible = route.Destination.Id == "dkp" ? records.Select(ContributionPayload.GenericV1) : records;
                await route.Destination.Client.Enqueue(compatible, token, expectedSettings: route.Settings); return (string?)null;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { return route.Destination.Name; }
        }));
        return failures.OfType<string>().ToArray();
    }

    internal void Retry()
    {
        foreach (var destination in All)
            if (destination.Client.Settings.Enabled && destination.Client.Status is { IsSending: false, PendingCount: > 0 }) destination.Client.Retry();
    }
    public void Dispose()
    {
        foreach (var destination in All) { destination.Client.Changed -= Notify; destination.Client.Dispose(); }
    }
}
