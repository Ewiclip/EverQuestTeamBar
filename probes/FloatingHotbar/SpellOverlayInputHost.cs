using System.Diagnostics;
namespace TeamBar;

internal static class SpellOverlayInputHost
{
    private static string Folder => Path.Combine(Path.GetDirectoryName(SpellOverlayStore.DefaultPath)!, "spell-input");
    internal static string ProfilePath => Path.Combine(Folder, "controls.json");
    internal static FileStream? TryBeginTransition(string? folder = null)
    {
        folder ??= Folder; Directory.CreateDirectory(folder);
        try { return new FileStream(Path.Combine(folder, "transition.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { return null; }
    }
    internal static string ControllerPath(string? applicationDirectory = null)
    {
        string appDirectory = Path.GetFullPath(applicationDirectory ?? AppContext.BaseDirectory);
        string? bundled = NativePair(appDirectory);
        if (bundled is not null) return bundled;
        // Prefer the current bridge at its existing path, including when a nearer
        // directory contains an older native build. Do not copy a resident DLL.
        foreach (string relative in new[] { "artifacts/team-bar-api-input", "artifacts/native-key-input/Release" })
        {
            for (var directory = new DirectoryInfo(appDirectory); directory is not null; directory = directory.Parent)
            {
                string? controller = NativePair(Path.Combine(directory.FullName, relative));
                if (controller is not null) return controller;
            }
        }
        throw new FileNotFoundException("The Team Bar input controller/bridge pair is missing. Restore the app build.");
    }
    private static string? NativePair(string directory)
    {
        directory = Path.GetFullPath(directory);
        string controller = Path.Combine(directory, "TeamControllerHost.dll");
        bool hostPresent = File.Exists(controller), bridgePresent = File.Exists(Path.Combine(directory, "TeamBridge.dll"));
        if (!hostPresent && !bridgePresent) return null;
        if (!hostPresent || !bridgePresent)
        {
            throw new FileNotFoundException($"The input controller/bridge pair is incomplete in '{directory}'. Restore both TeamControllerHost.dll and TeamBridge.dll from the same app build.");
        }
        return controller;
    }
    internal static async Task<AppInputSession> Start()
    {
        var setup = await Task.Run(CreateSetup);
        return await AppInputSession.Start(setup.Live, [], ProfilePath, setup.Log);
    }
    private static (LiveSetup Live, string Log) CreateSetup()
    {
        foreach (var process in Process.GetProcessesByName("TeamController"))
            using (process) throw new InvalidOperationException("Another input session is running. Connect through its updated Team Bar controls.");
        var overlays = SpellOverlayStore.Load(SpellOverlayStore.DefaultPath).Library.Overlays
            .Where(d => d.Enabled && d.HasClickMappings).ToArray();
        if (overlays.Length == 0) throw new InvalidOperationException("Save a spell key before connecting spell buttons.");
        var discovery = Identity.Discover(feature: MemoryFeature.SpellBarData);
        var owners = overlays.Select(d => d.Character).Distinct(StringComparer.OrdinalIgnoreCase).Select(name =>
        {
            var matches = discovery.Clients.Where(c => c.Character.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException($"Load exactly one {name} client before connecting spell buttons.");
            return matches[0];
        }).ToArray();
        var buttons = overlays.Any(d => d.DockGroups?.Length > 0) ? SpellDockRules.LoadButtons() : [];
        var clients = owners.Concat(SpellDockRules.RecipientsFor(overlays, buttons, discovery.Clients)).DistinctBy(c => c.ProcessId).ToArray();
        ClientSessions.ValidateAssignments(clients);
        var team = TeamEditing.MergeDetected(new(1, [], [], []), clients);
        var bindings = TeamEditing.Bind(team, clients); Routing.Validate(team, bindings);
        string folder = Folder; Directory.CreateDirectory(folder);
        var dockIds = overlays.SelectMany(d => d.DockGroups ?? []).SelectMany(g => g.ButtonIds).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var knownCharacters = overlays.Select(d => d.Character)
            .Concat(buttons.Where(b => dockIds.Contains(b.Value.Id)).SelectMany(b => b.Value.Characters).Select(c => c.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return (new LiveSetup(ControllerPath(), team, bindings, clients) { IdentityFeature = MemoryFeature.SpellBarData, AutoConnectCharacterNames = knownCharacters },
            Path.Combine(folder, $"session-{DateTime.Now:yyyyMMdd-HHmmss-fff}.jsonl"));
    }
}
