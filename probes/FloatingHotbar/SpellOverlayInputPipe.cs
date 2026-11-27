using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
namespace TeamBar;

internal sealed record SpellClickRequest(string Kind, long Window = 0, string? OverlayId = null,
    int SpellId = 0, SpellTargetKind Target = SpellTargetKind.Npc, long Foreground = 0,
    int ProcessId = 0, long Started = 0, uint TargetId = 0, string? TargetName = null, string? Class = null, int ZoneId = 0, string? Key = null,
    string? GroupId = null, string? ButtonId = null, string? ButtonRevision = null, string? Recipients = null);
internal sealed record SpellClickReply(bool Ok, string Message, bool Paused = true, string? Session = null, bool ReconnectRequired = false,
    bool RefreshCharactersRequired = false);

// One current-user endpoint belongs to the existing Team Bar session. It resolves
// saved spell mappings itself; clients cannot supply arbitrary recipients or keys.
internal sealed class SpellOverlayInputPipe : IDisposable
{
    internal const string Name = "TeamBar-SpellClicks-v1";
    private readonly CancellationTokenSource stop = new();
    private readonly NamedPipeServerStream server;
    private readonly Func<SpellClickRequest, int, Task<SpellClickReply>> handle;
    private readonly Action? closeHost;
    private readonly string session = Guid.NewGuid().ToString("N");
    private static readonly SemaphoreSlim requests = new(1);
    internal SpellOverlayInputPipe(Func<SpellClickRequest, int, Task<SpellClickReply>> handler, string pipeName = Name, Action? onCloseHost = null)
    {
        handle = handler; closeHost = onCloseHost;
        server = new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        // Request handlers explicitly marshal UI work. Transport failures should
        // never run their recovery loop on the application's dispatcher.
        _ = Task.Run(Serve);
    }
    private static async Task<string> Line(Stream stream, CancellationToken token)
    {
        var bytes = new List<byte>(); byte[] one = new byte[1];
        while (bytes.Count < 8192)
        {
            if (await stream.ReadAsync(one, token) == 0) throw new IOException("Spell button connection closed.");
            if (one[0] == 10) return Encoding.UTF8.GetString(bytes.ToArray());
            bytes.Add(one[0]);
        }
        throw new InvalidDataException("Spell button message is too large.");
    }
    private static Task Write(Stream stream, object value, CancellationToken token) =>
        stream.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value) + "\n"), token).AsTask();
    private async Task Serve()
    {
        while (!stop.IsCancellationRequested)
        {
            bool failed = false, reusable = true;
            try
            {
                await server.WaitForConnectionAsync(stop.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); timeout.CancelAfter(4500);
                if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out uint pid)) throw new IOException("Cannot identify the overlay app.");
                using var process = Process.GetProcessById((int)pid);
                if (!string.Equals(process.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Open the overlays and bar from the same app build.");
                var request = JsonSerializer.Deserialize<SpellClickRequest>(await Line(server, timeout.Token))
                    ?? throw new InvalidDataException("Invalid spell button request.");
                var reply = await handle(request, (int)pid).WaitAsync(timeout.Token);
                await Write(server, reply with { Session = session }, timeout.Token);
                bool received = await Line(server, timeout.Token) == "received";
                if (received && reply.Ok && request.Kind == "close-spell-host") closeHost?.Invoke();
            }
            catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException
                or JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { failed = true; }
            finally { reusable = PipeServerConnection.Reset(server); }
            if (!reusable || stop.IsCancellationRequested) return;
            // Bound retries even if an unexpected transport error is persistent.
            // Only listen for a new request; never replay an accepted action.
            if (failed) await Task.Delay(50).ConfigureAwait(false);
        }
    }
    internal static Task<SpellClickReply?> Request(SpellClickRequest request) => RequestNamed(request, Name);
    internal static async Task<SpellClickReply?> RequestNamed(SpellClickRequest request, string name)
    {
        await requests.WaitAsync();
        try { return await RequestCore(request, name); }
        finally { requests.Release(); }
    }
    private static async Task<SpellClickReply?> RequestCore(SpellClickRequest request, string name)
    {
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(4500);
        try { await pipe.ConnectAsync(300, timeout.Token); } catch (TimeoutException) { return null; }
        await Write(pipe, request, timeout.Token);
        var reply = JsonSerializer.Deserialize<SpellClickReply>(await Line(pipe, timeout.Token)) ?? throw new IOException("Invalid spell button response.");
        await pipe.WriteAsync("received\n"u8.ToArray(), timeout.Token);
        // Keep the request gate until the server has consumed our acknowledgment
        // and disconnected. A new client must not reuse this instance while the
        // previous request's finally block can still disconnect it. Never retry
        // a request: a tap may already have been delivered when transport fails.
        try
        {
            if (await pipe.ReadAsync(new byte[1], timeout.Token) != 0)
                throw new IOException("Unexpected data after the spell button response.");
        }
        catch (IOException error) when ((error.HResult & 0xffff) is 109 or 232) { }
        return reply;
    }
    public void Dispose() { stop.Cancel(); server.Dispose(); }
    [DllImport("kernel32.dll")] private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);
}
