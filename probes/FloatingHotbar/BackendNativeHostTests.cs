using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

internal static class BackendNativeHostTests
{
    internal static void Run(string output, string? library = null)
    {
        output = Path.GetFullPath(output);
        if (File.Exists(output)) throw new IOException("Native host test output must be new.");
        library ??= Path.Combine(Path.GetDirectoryName(TeamBar.SpellOverlayInputHost.ControllerPath())!, "TeamControllerHost.dll");
        library = Path.GetFullPath(library);
        int checks = Task.Run(() => Backend.NativeHostSelfTest(library)).GetAwaiter().GetResult();
        using var report = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(report, new { passed = true, gameplayInput = false, nativeLibrary = library, checks,
            tests = "actual managed/native ABI and UTF-8 commands; ready/status/cancel/overlay callbacks; GC survival; competing start cleanup; managed error delivery; callback timeout retention; joined stop and replacement; no games, windows, or keyboard input" });
    }
}

internal sealed partial class Backend
{
    // The only route to flag 1 has no LiveSetup, target window, process IDs or
    // bridge path. Production construction continues to use the normal flag 0.
    private Backend(string fixtureLibrary)
    {
        controllerLibrary = Path.GetFullPath(fixtureLibrary);
        clientPids = [];
        inputFreeFixture = true;
    }

    internal static async Task<int> NativeHostSelfTest(string library)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            ++checks;
            if (!condition) throw new InvalidOperationException(message);
        }
        void Reject(Action action, string message)
        {
            bool rejected = false;
            try { action(); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, message);
        }
        static async Task Stopped() => await WaitForNativeShutdown().WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);

        // Refuse to run in a process already hosting real input. A test must never
        // interfere with a live session or wait indefinitely for its disposal.
        Check(WaitForNativeShutdown().IsCompleted, "Native host tests require an idle Team Bar process.");
        var firstMessages = new NativeTestMessages();
        using (var first = new Backend(library))
        {
            first.Message += firstMessages.Record;
            Reject(() => first.Send("cancel"), "An unstarted backend accepted a command.");
            first.Start();
            Check(first.process is null, "Native backend created a controller process.");
            var ready = await firstMessages.Read("ready").ConfigureAwait(false);
            Check(ready.GetProperty("alwaysEnabled").GetBoolean() && !ready.GetProperty("paused").GetBoolean()
                && ready.GetProperty("spellOverlays").GetBoolean(), "Managed ready flags differ from the native protocol.");
            var initial = firstMessages.History.ToArray();
            Check(initial.Length >= 2 && initial[0].GetProperty("type").GetString() == "status"
                && initial[0].GetProperty("clients").GetArrayLength() == 0, "Initial status did not describe the input-free fixture.");
            Reject(first.Start, "The same backend started twice.");

            using (var competing = new Backend(library))
            {
                bool busy = false;
                try { competing.Start(); }
                catch (Win32Exception error) { busy = error.NativeErrorCode == 170; } // ERROR_BUSY
                Check(busy, "A second managed host bypassed the native session guard.");
                Check(competing.disposed, "Failed native startup did not dispose the managed backend.");
            }

            ForceNativeTestCollection();
            first.Send("cancel");
            Check((await firstMessages.Read("cancelled").ConfigureAwait(false)).GetRawText() == "{\"type\":\"cancelled\"}",
                "The callback delegate did not survive collection or cancellation JSON changed.");
            first.Send("overlays 41");
            var overlays = await firstMessages.Read("overlays").ConfigureAwait(false);
            Check(overlays.GetProperty("id").GetInt64() == 41 && overlays.GetProperty("ok").GetBoolean(),
                "Managed UTF-8 command marshaling or overlay reply changed.");
            Reject(() => first.Send("cancel\0extra"), "Managed Send accepted a NUL-truncated command.");
            Reject(() => first.Send("cancel\ncancel"), "Native Send accepted multiple commands in one call.");
            var status = await firstMessages.Read("status").ConfigureAwait(false);
            Check(!status.GetProperty("paused").GetBoolean() && status.GetProperty("clients").GetArrayLength() == 0,
                "Periodic native status was not delivered through the managed reader.");
            first.Dispose();
            await Stopped().ConfigureAwait(false);
            Reject(() => first.Send("cancel"), "A disposed backend accepted a command.");
            int afterStop = firstMessages.History.Count;
            await Task.Delay(75).ConfigureAwait(false);
            Check(firstMessages.History.Count == afterStop, "A disposed backend delivered a late callback.");
        }

        // The native callback intentionally blocks in this test writer. This
        // exercises the real P/Invoke timeout and root-retention path, not a fake
        // Stop delegate. Production writers are nonblocking channels.
        using (var blocked = new NativeTestBlockedWriter())
        {
            NativeHost? retained = null;
            try
            {
                retained = new NativeHost(library, null, [], 0, blocked, inputFreeFixture: true);
                await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                retained.Dispose();
                Check(!WaitForNativeShutdown().IsCompleted, "Timed-out native shutdown discarded a live callback.");
                var weak = new WeakReference<NativeHost>(retained);
                retained = null;
                ForceNativeTestCollection();
                Check(weak.TryGetTarget(out _), "A timed-out native host lost its managed callback/module roots.");
                blocked.Release();
                await Stopped().ConfigureAwait(false);
                Check(!blocked.TimedOut, "The test callback was not explicitly released before its deadline.");
                int afterJoin = blocked.Count;
                await Task.Delay(75).ConfigureAwait(false);
                Check(blocked.Count == afterJoin, "A native callback ran after Stop joined its worker.");
            }
            finally
            {
                blocked.Release();
                retained?.Dispose();
                await Stopped().ConfigureAwait(false);
            }
        }

        var failedMessages = new NativeTestMessages();
        using (var failed = new Backend(library))
        {
            failed.Message += failedMessages.Record;
            failed.Start();
            await failedMessages.Read("ready").ConfigureAwait(false);
            // Include non-ASCII bytes so this also exercises LPUTF8Str marshaling.
            failed.Send("unsupported-\u00e9");
            var error = await failedMessages.Read("error").ConfigureAwait(false);
            Check(error.GetProperty("message").GetString() == "Unknown controller command", "Native parser errors were not delivered to managed subscribers.");
        }
        await Stopped().ConfigureAwait(false);

        var replacementMessages = new NativeTestMessages();
        using (var replacement = new Backend(library))
        {
            replacement.Message += replacementMessages.Record;
            replacement.Start();
            await replacementMessages.Read("ready").ConfigureAwait(false);
            ForceNativeTestCollection();
            replacement.Send("cancel");
            await replacementMessages.Read("cancelled").ConfigureAwait(false);
            Check(replacement.process is null, "Replacement spawned a controller process.");
        }
        await Stopped().ConfigureAwait(false);
        Check(WaitForNativeShutdown().IsCompleted, "Final managed host cleanup remains pending.");
        return checks;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceNativeTestCollection()
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    }

    private sealed class NativeTestMessages
    {
        private readonly Channel<JsonElement> pending = Channel.CreateUnbounded<JsonElement>();
        internal ConcurrentQueue<JsonElement> History { get; } = new();
        internal void Record(JsonElement message)
        {
            var copy = message.Clone();
            History.Enqueue(copy); pending.Writer.TryWrite(copy);
        }
        internal async Task<JsonElement> Read(string type)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                var message = await pending.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
                string? actual = message.GetProperty("type").GetString();
                if (actual == type) return message;
                if (actual == "error") throw new InvalidOperationException("Unexpected native host error: " + message.GetRawText());
            }
        }
    }

    private sealed class NativeTestBlockedWriter : ChannelWriter<string>, IDisposable
    {
        private readonly ManualResetEventSlim release = new(false);
        private int count, timedOut;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Count => Volatile.Read(ref count);
        internal bool TimedOut => Volatile.Read(ref timedOut) != 0;
        public override bool TryWrite(string item)
        {
            Interlocked.Increment(ref count);
            if (item.Contains("\"type\":\"ready\"", StringComparison.Ordinal))
            {
                Entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) Interlocked.Exchange(ref timedOut, 1);
            }
            return true;
        }
        public override bool TryComplete(Exception? error = null) => true;
        public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(!cancellationToken.IsCancellationRequested);
        internal void Release() => release.Set();
        public void Dispose() { release.Set(); release.Dispose(); }
    }
}
