namespace TeamBar;

internal static class NativePathTests
{
    internal static void Run(string output)
    {
        string temp = Path.GetFullPath(Path.GetTempPath());
        string root = Path.Combine(temp, "teambar-native-paths-" + Guid.NewGuid().ToString("N"));
        var checks = new List<string>();
        string Fixture(string name) => Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        static string Pair(string directory, bool host = true, bool bridge = true)
        {
            Directory.CreateDirectory(directory);
            if (host) File.WriteAllText(Path.Combine(directory, "TeamControllerHost.dll"), "fixture");
            if (bridge) File.WriteAllText(Path.Combine(directory, "TeamBridge.dll"), "fixture");
            return Path.Combine(directory, "TeamControllerHost.dll");
        }
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks.Add(message);
        }
        void Reject(string app, string expectedPath, string message)
        {
            try { SpellOverlayInputHost.ControllerPath(app); }
            catch (FileNotFoundException error) when (error.Message.Contains("incomplete", StringComparison.Ordinal)
                && error.Message.Contains(expectedPath, StringComparison.OrdinalIgnoreCase))
            { checks.Add(message); return; }
            throw new InvalidOperationException(message);
        }
        try
        {
            string current = Fixture("current"), app = Path.Combine(current, "builds", "Team Bar");
            Directory.CreateDirectory(app);
            string api = Pair(Path.Combine(current, "artifacts", "team-bar-api-input"));
            Pair(Path.Combine(current, "artifacts", "native-key-input", "Release"));
            Check(SpellOverlayInputHost.ControllerPath(app) == api, "Current API pair wins over legacy and retains its exact existing path.");
            Pair(Path.Combine(app, "artifacts", "native-key-input", "Release"));
            Check(SpellOverlayInputHost.ControllerPath(app) == api, "A nearer legacy directory cannot override the workspace API pair.");
            string bundle = Pair(app);
            Check(SpellOverlayInputHost.ControllerPath(app) == bundle, "A complete app bundle takes precedence.");

            string old = Fixture("legacy"), oldApp = Path.Combine(old, "builds", "Team Bar");
            Directory.CreateDirectory(oldApp);
            string legacy = Pair(Path.Combine(old, "artifacts", "native-key-input", "Release"));
            Check(SpellOverlayInputHost.ControllerPath(oldApp) == legacy, "An older setup with no API pair keeps the legacy fallback.");

            foreach (bool hostOnly in new[] { true, false })
            {
                string incomplete = Fixture(hostOnly ? "api-host-only" : "api-bridge-only");
                string native = Path.Combine(incomplete, "artifacts", "team-bar-api-input");
                Pair(native, hostOnly, !hostOnly);
                Pair(Path.Combine(incomplete, "artifacts", "native-key-input", "Release"));
                Reject(incomplete, native, "An incomplete API pair fails clearly instead of silently using legacy (hostOnly=" + hostOnly + ").");

                string partialBundle = Fixture(hostOnly ? "bundle-host-only" : "bundle-bridge-only");
                Pair(partialBundle, hostOnly, !hostOnly);
                Pair(Path.Combine(partialBundle, "artifacts", "team-bar-api-input"));
                Reject(partialBundle, partialBundle, "An incomplete bundle fails clearly instead of mixing native builds (hostOnly=" + hostOnly + ").");
            }
            string missing = Fixture("missing");
            try { SpellOverlayInputHost.ControllerPath(missing); throw new InvalidOperationException("Missing native pair was accepted."); }
            catch (FileNotFoundException error) when (error.Message.Contains("missing", StringComparison.Ordinal))
            { checks.Add("A missing native pair reports a restore error."); }
            CollectorFormat.WriteNew(output, new { passed = true, checks, gameInputSent = false, processMemoryRead = false, processMemoryWritten = false });
        }
        finally
        {
            string resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(temp.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("teambar-native-paths-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to remove an unexpected native-path fixture directory.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }
}
