using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace TeamBar;

internal static class AppInstanceCoordinatorTests
{
    internal static void Run(string output) => Task.Run(() => RunAsync(output)).GetAwaiter().GetResult();
    private static async Task RunAsync(string output)
    {
        string full = Path.GetFullPath(output), folder = full + ".files";
        if (File.Exists(full) || Directory.Exists(folder)) throw new IOException("App instance test output must be new.");
        Directory.CreateDirectory(folder);
        string scope = Path.Combine(folder, "app-instance");
        var checks = new List<string>();
        void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException(message); checks.Add(message); }
        var delivered = new List<string[]>();
        Task Route(string[] args) { delivered.Add(args); return Task.CompletedTask; }
        Task UnexpectedRoute(string[] _) => throw new InvalidOperationException("A secondary launcher became the owner.");
        using (var owner = await AppInstanceCoordinator.StartOrForward(scope, ["--setup-current"], Route))
        {
            Check(owner is not null && delivered.Count == 0, "primary owns the scope without routing initial arguments");
            string[][] requests = [["--setup-current"], ["--spell-overlays"], ["--items"]];
            foreach (string[] args in requests)
            {
                using var secondary = await AppInstanceCoordinator.StartOrForward(scope, args, UnexpectedRoute);
                Check(secondary is null, "secondary launcher forwards to the owner: " + args[0]);
            }
            Check(delivered.Count == requests.Length && delivered.Zip(requests).All(pair => pair.First.SequenceEqual(pair.Second)),
                "one owner routes each of three launch requests exactly once and preserves arguments");
            using var empty = await AppInstanceCoordinator.StartOrForward(scope, [], UnexpectedRoute);
            Check(empty is null && delivered.Count == 4 && delivered[^1].Length == 0,
                "an empty launch request reaches the existing owner unchanged");
            bool rejected = false;
            try { using var oversized = await AppInstanceCoordinator.StartOrForward(scope, [new string('x', 4097)], UnexpectedRoute); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected && delivered.Count == 4, "oversized arguments are rejected before delivery");
        }
        int replacementCalls = 0;
        using (var replacement = await AppInstanceCoordinator.StartOrForward(scope, ["--items"], _ =>
            { replacementCalls++; return Task.CompletedTask; }))
        {
            Check(replacement is not null && replacementCalls == 0, "disposing the owner releases the file lock and pipe for a new owner");
            using var secondary = await AppInstanceCoordinator.StartOrForward(scope, ["--spell-overlays"], UnexpectedRoute);
            Check(secondary is null && replacementCalls == 1 && delivered.Count == 4,
                "forwarding after takeover reaches only the replacement owner");
        }
        await CrossProcess(folder, Check);
        await CheckInstallerExit(folder, Check);
        CollectorFormat.WriteNew(full, new { passed = true, tests = checks, crossProcess = true, gameInputSent = false,
            processMemoryRead = false, processMemoryWritten = false, windowsOpened = false });
    }

    private sealed record FixtureReport(int ProcessId, bool Owner, string[][] Routed);

    // This diagnostic path is intentionally independent of TeamBarApplication:
    // no application UI, user preferences, clients or native input are opened.
    internal static void RunFixture(string scope, string report, string[] arguments)
    {
        if (arguments is ["--installer-exit-fixture"]) { RunInstallerExitFixture(report); return; }
        Task.Run(async () =>
        {
            if (File.Exists(report)) throw new IOException("Instance fixture report must be new.");
            var routed = new ConcurrentQueue<string[]>();
            using var owner = await AppInstanceCoordinator.StartOrForward(scope, arguments, args =>
            { routed.Enqueue(args.ToArray()); return Task.CompletedTask; });
            if (owner is not null)
            {
                Console.WriteLine(JsonSerializer.Serialize(new FixtureReport(Environment.ProcessId, true, [])));
                await Console.Out.FlushAsync();
                string? command = await Console.In.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(45));
                if (command != "exit") throw new InvalidDataException("Expected the isolated fixture exit command.");
            }
            CollectorFormat.WriteNew(report, new FixtureReport(Environment.ProcessId, owner is not null, routed.ToArray()));
        }).GetAwaiter().GetResult();
    }

    private static void RunInstallerExitFixture(string report)
    {
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        int requests = 0;
        InstallerExitSignal? listener = null;
        using var active = InstallerExitSignal.Listen(dispatcher, () =>
        {
            if (!dispatcher.CheckAccess() || !listener!.IsRequested) throw new InvalidOperationException("Expected a latched Setup request on the host dispatcher.");
            if (++requests == 1)
            {
                listener.AllowRetry(); // Model an edit that could not be saved.
                Console.WriteLine("retry");
                Console.Out.Flush();
            }
            else dispatcher.InvokeShutdown();
        }) ?? throw new IOException("Could not create the fixture's Setup signal.");
        listener = active;
        Console.WriteLine(JsonSerializer.Serialize(new FixtureReport(Environment.ProcessId, true, [])));
        Console.Out.Flush();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var stop = timeout.Token.Register(() => dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Normal));
        System.Windows.Threading.Dispatcher.Run();
        if (requests != 2) throw new TimeoutException("The fixture did not receive two Setup requests.");
        CollectorFormat.WriteNew(report, new FixtureReport(Environment.ProcessId, true, [["installer-exit"]]));
    }

    private static async Task CheckInstallerExit(string folder, Action<bool, string> check)
    {
        var report = Path.Combine(folder, "installer-exit.json");
        await using var host = new FixtureProcess(Path.Combine(folder, "installer-scope"), report, ["--installer-exit-fixture"]);
        var ready = await host.Ready();
        if (ready.ProcessId != host.Process.Id || ready.ProcessId == Environment.ProcessId) throw new InvalidOperationException("Unexpected fixture process.");
        using var signal = EventWaitHandle.OpenExisting($@"Local\TeamBar.Setup.Exit.{ready.ProcessId}");
        signal.Set();
        check(await host.Process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) == "retry" && !signal.WaitOne(0),
            "A Setup request reaches the chosen fixture and can be retried after a declined shutdown.");
        signal.Set();
        await host.Completed();
        check(ReadReport(report).Routed is [["installer-exit"]], "A repeated Setup request completes orderly dispatcher shutdown in that process.");
    }

    private static async Task CrossProcess(string folder, Action<bool, string> check)
    {
        string scope = Path.Combine(folder, "cross-process", "application");
        string ownerReport = Path.Combine(folder, "owner-process.json");
        string[][] requests = [["--setup-current", "C:\\fixture path\\team.json", "C:\\fixture controller\\TeamControllerHost.dll"],
            ["--spell-overlays"], ["--items", "Fixture \"quoted\" argument", "Unicode Ω"], []];
        await using (var owner = new FixtureProcess(scope, ownerReport, ["--setup-current"]))
        {
            var ready = await owner.Ready();
            check(ready.Owner && ready.ProcessId == owner.Process.Id && ready.ProcessId != Environment.ProcessId,
                "a separate application process owns the isolated activation scope");
            for (int i = 0; i < requests.Length; i++)
            {
                string report = Path.Combine(folder, $"forwarded-process-{i}.json");
                await using var secondary = new FixtureProcess(scope, report, requests[i]);
                await secondary.Completed();
                var forwarded = ReadReport(report);
                check(secondary.Process.HasExited && forwarded.ProcessId == secondary.Process.Id && !forwarded.Owner && forwarded.Routed.Length == 0,
                    "secondary process forwards and exits without taking ownership: " + i);
                check(!owner.Process.HasExited, "forwarding leaves the primary process running: " + i);
            }
            await owner.Exit();
            var delivered = ReadReport(ownerReport);
            check(delivered.Owner && delivered.ProcessId == owner.Process.Id && delivered.Routed.Length == requests.Length
                && delivered.Routed.Zip(requests).All(pair => pair.First.SequenceEqual(pair.Second)),
                "the owner routes each cross-process request exactly once, preserving empty, spaced, quoted and Unicode arguments");
        }

        string replacementReport = Path.Combine(folder, "replacement-process.json");
        await using (var replacement = new FixtureProcess(scope, replacementReport, ["--items"]))
        {
            var ready = await replacement.Ready();
            check(ready.Owner && ready.ProcessId == replacement.Process.Id,
                "a new process recovers activation ownership after the prior owner exits");
            string report = Path.Combine(folder, "replacement-forwarded-process.json");
            await using (var secondary = new FixtureProcess(scope, report, ["--spell-overlays"]))
            {
                await secondary.Completed();
                check(!ReadReport(report).Owner, "a subsequent launcher forwards to the replacement owner and exits");
            }
            await replacement.Exit();
            var delivered = ReadReport(replacementReport);
            check(delivered.Routed.Length == 1 && delivered.Routed[0].SequenceEqual(new[] { "--spell-overlays" })
                && ReadReport(ownerReport).Routed.Length == requests.Length,
                "after takeover only the replacement receives the next activation, once");
        }
        using var released = new FileStream(scope + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        check(released.Length == 0, "all fixture processes exit and release the activation ownership lock");
    }
    private static FixtureReport ReadReport(string path) => JsonSerializer.Deserialize<FixtureReport>(File.ReadAllText(path), CollectorFormat.Json)
        ?? throw new InvalidDataException("Missing application instance fixture report.");

    private sealed class FixtureProcess : IAsyncDisposable
    {
        internal Process Process { get; }
        private readonly Task<string> errors;
        internal FixtureProcess(string scope, string report, string[] arguments)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string value in new[] { "--app-instance-fixture", scope, report }.Concat(arguments)) start.ArgumentList.Add(value);
            Process = System.Diagnostics.Process.Start(start) ?? throw new IOException("Could not start the isolated activation fixture.");
            errors = Process.StandardError.ReadToEndAsync();
        }
        internal async Task<FixtureReport> Ready()
        {
            string? line = await Process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (line is null) throw new IOException("Activation fixture exited before readiness: " + await errors);
            return JsonSerializer.Deserialize<FixtureReport>(line) ?? throw new InvalidDataException("Invalid activation fixture readiness.");
        }
        internal async Task Completed()
        {
            await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            string detail = await errors;
            if (Process.ExitCode != 0) throw new IOException("Activation fixture failed: " + detail);
        }
        internal async Task Exit()
        {
            await Process.StandardInput.WriteLineAsync("exit");
            await Process.StandardInput.FlushAsync();
            await Completed();
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                // Only terminate this explicitly spawned fixture if a failed
                // assertion or timeout prevented the normal exit handshake.
                if (!Process.HasExited) { Process.Kill(); await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                await errors;
            }
            finally { Process.Dispose(); }
        }
    }
}
