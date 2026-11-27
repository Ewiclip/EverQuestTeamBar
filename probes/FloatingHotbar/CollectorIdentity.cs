using System.ComponentModel;
using System.Diagnostics;

internal static class CollectorIdentity
{
    internal static DetectedClient Read(int pid)
    {
#if TEAMBAR_APPLICATION
        if (!TeamBar.GameDataAccess.IsEnabled(MemoryFeature.DataCollection)) throw new InvalidDataException(TeamBar.GameDataAccess.DisabledReason(MemoryFeature.DataCollection));
#endif
        using var process = Process.GetProcessById(pid);
        string path = process.MainModule?.FileName ?? throw new InvalidDataException("The client executable is unavailable.");
        var build = CollectorBuild.ForPath(path);
        try
        {
            if (build == CollectorBuild.Live) return Identity.Read(pid, MemoryFeature.DataCollection);
            using var memory = build.Open(pid, process.StartTime.ToUniversalTime().Ticks, CollectorCapability.Identity);
            return Identity.Read(memory, build.IdentityLayout, build.Channel, build.BuildSha256);
        }
        catch (Exception error) when (error is InvalidDataException or Win32Exception or InvalidOperationException or ArgumentException or IOException)
        {
            // Known installation/channel context explains rejected clients without
            // exposing executable command lines or accepting unverified identity.
            throw new InvalidDataException($"{build.Channel} client: {error.Message}", error);
        }
    }
    internal static Discovery Discover()
    {
#if TEAMBAR_APPLICATION
        if (!TeamBar.GameDataAccess.IsEnabled(MemoryFeature.DataCollection)) return new([], []);
#endif
        return Discover(Identity.ProcessIds(), Read);
    }
    internal static Discovery Discover(IEnumerable<int> processIds, Func<int, DetectedClient> read)
    {
        var found = new List<DetectedClient>(); var errors = new List<string>();
        foreach (int pid in processIds)
        {
            try
            {
                var client = read(pid);
                CollectorBuild.ForClient(client).Require(CollectorCapability.Identity);
                found.Add(client);
            }
            catch (Exception error) when (error is InvalidDataException or Win32Exception or InvalidOperationException or ArgumentException or IOException)
            { errors.Add($"Client PID {pid} unavailable: {error.Message}"); }
        }
        return new(found.OrderBy(c => c.Left).ToArray(), errors.ToArray());
    }
}
