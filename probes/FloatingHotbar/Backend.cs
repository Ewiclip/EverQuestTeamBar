using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;

internal sealed partial class Backend : IDisposable
{
    private readonly Process? process;
    private readonly string? controllerLibrary;
    private readonly string? bridgeLibrary;
    internal string? SelectedBridgePath => bridgeLibrary;
    private readonly int[] clientPids;
    private readonly ulong barWindow;
    private readonly bool inputFreeFixture;
    private readonly object lifecycle = new();
    private readonly Channel<string> commands = Channel.CreateBounded<string>(32);
    private readonly Channel<string> nativeMessages = Channel.CreateUnbounded<string>(new()
    { SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = false });
    private NativeHost? nativeHost;
    private bool started;
    private volatile bool disposed;
    internal event Action<JsonElement>? Message;
    internal Backend(LiveSetup setup, nint bar)
    {
        inputFreeFixture = false;
        foreach (var binding in setup.Clients)
        {
            using var game = Process.GetProcessById(binding.ProcessId);
            if (game.StartTime.ToUniversalTime().Ticks != binding.StartedUtcTicks)
                throw new InvalidDataException("A selected EQ session changed. Refresh the client bindings.");
        }
        clientPids = setup.Clients.Select(client => client.ProcessId).ToArray();
        barWindow = unchecked((ulong)bar.ToInt64());
        string controller = Path.GetFullPath(setup.Controller);
        string name = Path.GetFileName(controller);
        if (string.Equals(name, "TeamController.exe", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "TeamControllerHost.dll", StringComparison.OrdinalIgnoreCase))
        {
            string directory = Path.GetDirectoryName(controller)!;
            controllerLibrary = string.Equals(name, "TeamControllerHost.dll", StringComparison.OrdinalIgnoreCase)
                ? controller : Path.Combine(directory, "TeamControllerHost.dll");
            bridgeLibrary = Path.Combine(directory, "TeamBridge.dll");
            return;
        }
        if (!string.Equals(name, "BarUiTestController.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unsupported input controller. Select TeamControllerHost.dll from the Team Bar installation.");

        // The isolated UI fixture deliberately retains its process/stdin protocol.
        var start = new ProcessStartInfo(controller) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(bar.ToInt64().ToString(CultureInfo.InvariantCulture));
        foreach (var client in setup.Clients) start.ArgumentList.Add(client.ProcessId.ToString(CultureInfo.InvariantCulture));
        process = new Process { StartInfo = start };
    }
    internal void Start()
    {
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (started) throw new InvalidOperationException("The input controller has already been started.");
            started = true;
            try
            {
                if (process is not null)
                {
                    if (!process.Start()) throw new InvalidOperationException("Cannot start the input controller.");
                    _ = Task.Run(Read); _ = Task.Run(Write); _ = Task.Run(ReadErrors);
                }
                else
                {
                    nativeHost = new NativeHost(controllerLibrary!, bridgeLibrary, clientPids, barWindow, nativeMessages.Writer, inputFreeFixture);
                    _ = Task.Run(ReadNative);
                }
            }
            catch { Dispose(); throw; }
        }
    }
    private void Error(string text)
    {
        if (disposed) return;
        Message?.Invoke(JsonSerializer.SerializeToElement(new { type = "error", message = text }));
    }
    private async Task Read()
    {
        try
        {
            while (await process!.StandardOutput.ReadLineAsync() is { } line)
            {
                using var data = JsonDocument.Parse(line); Message?.Invoke(data.RootElement.Clone());
            }
            if (!disposed) Error("The input controller disconnected. The bar has been closed.");
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or JsonException) { Error(e.Message); }
    }
    private async Task ReadErrors()
    {
        try { string text = await process!.StandardError.ReadToEndAsync(); if (!string.IsNullOrWhiteSpace(text)) Error(text.Trim()); }
        catch (Exception e) when (e is IOException or InvalidOperationException) { Error(e.Message); }
    }
    private async Task Write()
    {
        try { await foreach (string line in commands.Reader.ReadAllAsync()) { await process!.StandardInput.WriteLineAsync(line); await process!.StandardInput.FlushAsync(); } }
        catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException) { Error(e.Message); }
    }
    private async Task ReadNative()
    {
        await foreach (string line in nativeMessages.Reader.ReadAllAsync())
        {
            if (disposed) return;
            try
            {
                using var data = JsonDocument.Parse(line);
                Message?.Invoke(data.RootElement.Clone());
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException) { Error(e.Message); }
        }
    }
    internal void Send(string command)
    {
        lock (lifecycle)
        {
            if (disposed || !started || command.IndexOf('\0') >= 0 ||
                (process is not null ? !commands.Writer.TryWrite(command) : nativeHost?.Send(command) != true))
                throw new InvalidOperationException("The controller is unavailable or its command queue is full.");
        }
    }
    internal void Tap(long id, RoutedTap[] targets)
    {
        if (targets.Select(t => t.ProcessId).Distinct().Count() != targets.Length)
            throw new InvalidOperationException("Send repeated character mappings in separate acknowledged steps.");
        bool chord = targets.Any(t => t.Modifiers != 0);
        Send((chord ? "chord " : "tap ") + id.ToString(CultureInfo.InvariantCulture) + " "
            + string.Join(" ", targets.Select(t => $"{t.ProcessId} {t.Scan}" + (chord ? $" {t.Modifiers}" : ""))));
    }
    internal void SpellTap(long id, RoutedTap target) => Send($"spell-chord {id} {target.ProcessId} {target.Scan} {target.Modifiers}");
    // Apply may replace an input session in the same process. Await this after
    // closing the old workspace so its bridges have drained before Start again.
    internal static Task WaitForNativeShutdown() => NativeHost.WaitForShutdown();
    public void Dispose()
    {
        lock (lifecycle)
        {
            if (disposed) return; disposed = true;
            commands.Writer.TryComplete(); nativeMessages.Writer.TryComplete();
            nativeHost?.Dispose(); nativeHost = null;
            if (process is null) return;
            try { process.StandardInput.Close(); if (!process.WaitForExit(5000)) process.Kill(); }
            catch (InvalidOperationException) { }
            process.Dispose();
        }
    }

    private sealed class NativeHost : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct HostConfig
        {
            internal uint Size, Version, OwnerPid, ClientCount;
            internal ulong BarWindow;
            internal nint ClientPids, BridgePath;
            internal uint Flags, Reserved;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void NativeCallback(nint jsonUtf8, nint context);
        [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
        private delegate nint NativeStart(ref HostConfig config, NativeCallback callback, nint context);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int NativeSend(nint host, [MarshalAs(UnmanagedType.LPUTF8Str)] string commandUtf8);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int NativeStop(nint host, uint timeoutMs);

        // A native function pointer does not root its managed delegate. Keep each host
        // alive until Stop confirms the worker and every callback have finished.
        private static readonly ConcurrentDictionary<NativeHost, byte> active = new();
        private readonly object lifecycle = new();
        private readonly NativeCallback callback;
        private readonly NativeSend send;
        private readonly NativeStop stop;
        private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private nint module, host;
        private bool stopping;

        internal NativeHost(string library, string? bridge, int[] pids, ulong bar, ChannelWriter<string> messages, bool inputFreeFixture = false)
        {
            if (inputFreeFixture && (bridge is not null || pids.Length != 0 || bar != 0))
                throw new ArgumentException("The native fixture cannot enroll clients, windows, or a bridge.");
            // Never call UI subscribers on the native worker. Dispose can safely join
            // it while the UI thread is closing its own windows.
            callback = (jsonUtf8, _) =>
            {
                try
                {
                    string? json = Marshal.PtrToStringUTF8(jsonUtf8);
                    if (json is not null) messages.TryWrite(json);
                }
                catch (Exception) { /* Managed exceptions must not cross the native callback boundary. */ }
            };
            module = NativeLibrary.Load(library);
            try
            {
                var start = Marshal.GetDelegateForFunctionPointer<NativeStart>(NativeLibrary.GetExport(module, "TeamControllerStart"));
                send = Marshal.GetDelegateForFunctionPointer<NativeSend>(NativeLibrary.GetExport(module, "TeamControllerSend"));
                stop = Marshal.GetDelegateForFunctionPointer<NativeStop>(NativeLibrary.GetExport(module, "TeamControllerStop"));
                var pinnedPids = GCHandle.Alloc(pids, GCHandleType.Pinned);
                nint bridgePath = 0;
                try
                {
                    if (bridge is not null) bridgePath = Marshal.StringToHGlobalUni(bridge);
                    var config = new HostConfig
                    {
                        Size = checked((uint)Marshal.SizeOf<HostConfig>()), Version = 1,
                        OwnerPid = checked((uint)Environment.ProcessId), ClientCount = checked((uint)pids.Length),
                        BarWindow = bar, ClientPids = pids.Length == 0 ? 0 : pinnedPids.AddrOfPinnedObject(), BridgePath = bridgePath,
                        Flags = inputFreeFixture ? 1u : 0u, Reserved = 0
                    };
                    active.TryAdd(this, 0);
                    host = start(ref config, callback, 0);
                    if (host == 0)
                        throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot start the input controller host.");
                }
                finally
                {
                    if (bridgePath != 0) Marshal.FreeHGlobal(bridgePath);
                    pinnedPids.Free();
                }
            }
            catch
            {
                if (host != 0) Dispose();
                else
                {
                    // A failed Start returns no worker and no surviving callbacks.
                    active.TryRemove(this, out _);
                    NativeLibrary.Free(module); module = 0;
                    stopped.TrySetResult();
                }
                throw;
            }
        }

        internal bool Send(string command)
        {
            lock (lifecycle) return !stopping && host != 0 && send(host, command) == 1;
        }

        internal static Task WaitForShutdown() => Task.WhenAll(active.Keys.Select(value => value.stopped.Task));

        public void Dispose()
        {
            lock (lifecycle)
            {
                if (stopping) return;
                stopping = true;
                try { if (Stop(1000)) return; }
                catch (Exception) { /* Retain the native worker's roots and retry cleanup below. */ }
            }
            _ = Task.Run(FinishStopping);
        }

        private async Task FinishStopping()
        {
            while (true)
            {
                lock (lifecycle)
                {
                    try { if (Stop(15000)) return; }
                    catch (Exception) { /* Never unload a module while its worker may still run. */ }
                }
                await Task.Delay(1000).ConfigureAwait(false);
            }
        }

        // The lifecycle lock makes Stop calls mutually exclusive with Send and retries.
        private bool Stop(uint timeoutMs)
        {
            if (host != 0)
            {
                if (stop(host, timeoutMs) != 1) return false;
                host = 0;
            }
            if (module != 0) { NativeLibrary.Free(module); module = 0; }
            active.TryRemove(this, out _);
            GC.KeepAlive(callback);
            stopped.TrySetResult();
            return true;
        }
    }
}
