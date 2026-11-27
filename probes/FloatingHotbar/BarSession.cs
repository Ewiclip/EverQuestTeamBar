using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal sealed record BarState(int ProcessId, long StartedUtcTicks, bool Ready, bool Cleared = false);

// This endpoint can query, clear hotkeys, or close its own bar. It never sends gameplay input.
internal sealed class BarSession : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly NamedPipeServerStream pipe;
    private readonly Func<bool> ready;
    private readonly Action close;
    private readonly Func<Task>? clearHotkeys;
    private readonly Func<bool>? closeAfterClear;
    private readonly int pid = Environment.ProcessId;
    private readonly long started;
    internal static string Name(string profile) => "EqTeamBar-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(profile).ToUpperInvariant())))[..32];
    internal BarSession(string profile, Func<bool> isReady, Action closeBar, Func<Task>? clear = null, Func<bool>? shouldCloseAfterClear = null)
    {
        ready = isReady; close = closeBar; clearHotkeys = clear; closeAfterClear = shouldCloseAfterClear;
        using var self = Process.GetCurrentProcess(); started = self.StartTime.ToUniversalTime().Ticks;
        pipe = new(Name(profile), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        _ = Serve();
    }
    private static async Task<string> ReadLine(Stream stream, CancellationToken token)
    {
        var bytes = new List<byte>(); var b = new byte[1];
        while (bytes.Count < 512)
        {
            if (await stream.ReadAsync(b, token) == 0) throw new IOException("The bar connection closed.");
            if (b[0] == 10) return Encoding.UTF8.GetString(bytes.ToArray());
            bytes.Add(b[0]);
        }
        throw new InvalidDataException("Bar message is too long.");
    }
    private static Task WriteLine(Stream stream, string line, CancellationToken token) => stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), token).AsTask();
    private async Task Serve()
    {
        while (!stop.IsCancellationRequested)
        {
            bool closing = false, failed = false, reusable = true;
            try
            {
                await pipe.WaitForConnectionAsync(stop.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); timeout.CancelAfter(3000);
                string request = await ReadLine(pipe, timeout.Token);
                if (request is "status" or "close" or "clear-hotkeys")
                {
                    bool cleared = request == "clear-hotkeys" && clearHotkeys is not null;
                    if (cleared) await clearHotkeys!().WaitAsync(timeout.Token);
                    bool closeAfterReply = request == "close" || cleared && closeAfterClear?.Invoke() == true;
                    await WriteLine(pipe, JsonSerializer.Serialize(new BarState(pid, started, ready(), cleared)), timeout.Token);
                    // Do not disconnect while the reply is still buffered, or close without a received acknowledgment.
                    closing = await ReadLine(pipe, timeout.Token) == "received" && closeAfterReply;
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException) { failed = true; }
            finally { reusable = TeamBar.PipeServerConnection.Reset(pipe); }
            if (closing) { close(); return; }
            if (!reusable || stop.IsCancellationRequested) return;
            if (failed) await Task.Delay(50);
        }
    }
    internal static async Task<BarState?> Request(string profile, string command)
    {
        using var client = new NamedPipeClientStream(".", Name(profile), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await client.ConnectAsync(400); } catch (TimeoutException) { return null; }
        using var timeout = new CancellationTokenSource(4000);
        await WriteLine(client, command, timeout.Token);
        var state = JsonSerializer.Deserialize<BarState>(await ReadLine(client, timeout.Token)) ?? throw new InvalidDataException("Invalid bar response.");
        await WriteLine(client, "received", timeout.Token);
        return state;
    }
    internal static async Task<bool> ClearHotkeys(string profile)
    {
        var state = await Request(profile, "clear-hotkeys");
        if (state is null) return false;
        if (!state.Cleared) throw new IOException("The running controls need an update before clearing hotkeys. Close them, then apply again.");
        return true;
    }
    internal static async Task<bool> CloseExisting(string profile)
    {
        var state = await Request(profile, "close"); if (state is null) return false;
        Process old;
        try { old = Process.GetProcessById(state.ProcessId); } catch (ArgumentException) { return true; }
        using (old)
        {
            try { if (old.HasExited || old.StartTime.ToUniversalTime().Ticks != state.StartedUtcTicks) return true; }
            catch (InvalidOperationException) when (old.HasExited) { return true; }
            using var timeout = new CancellationTokenSource(15000);
            try { await old.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { throw new IOException("The previous bar is still releasing input. Wait a moment, then apply again."); }
        }
        return true;
    }
    internal static async Task WaitReady(string profile, Process process)
    {
        long until = Environment.TickCount64 + 15000;
        while (Environment.TickCount64 < until)
        {
            if (process.HasExited) throw new IOException("The bar did not start. Check the startup message or session log.");
            var state = await Request(profile, "status");
            if (state is not null && state.ProcessId == process.Id && state.StartedUtcTicks == process.StartTime.ToUniversalTime().Ticks && state.Ready) return;
            await Task.Delay(150);
        }
        throw new IOException("The bar has not finished connecting. Check its startup message before applying again.");
    }
    public void Dispose() { stop.Cancel(); pipe.Dispose(); }
}
