using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace TeamBar;

// Launch requests only. The app host validates commands and dispatches UI work;
// this coordinator never interprets arguments as input-controller commands.
internal sealed class AppInstanceCoordinator : IDisposable
{
    private const int MaximumArguments = 16, MaximumArgumentLength = 4096, MaximumMessageBytes = 65536;
    private sealed record LaunchReply(bool Ok, string? Error = null);
    private readonly FileStream ownership;
    private readonly NamedPipeServerStream server;
    private readonly CancellationTokenSource stop = new();
    private readonly Func<string[], Task> route;
    private readonly Task serving;
    private int disposed;
    internal static string Name(string scopePath) => "TeamBar-App-" + Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(Path.GetFullPath(scopePath).ToUpperInvariant())));

    private AppInstanceCoordinator(FileStream ownership, string name, Func<string[], Task> route)
    {
        this.ownership = ownership; this.route = route;
        server = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        serving = Serve();
    }

    internal static async Task<AppInstanceCoordinator?> StartOrForward(string scopePath, string[] args, Func<string[], Task> route)
    {
        ArgumentNullException.ThrowIfNull(route);
        ValidateArguments(args);
        string full = Path.GetFullPath(scopePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string name = Name(full);
        FileStream ownership;
        try { ownership = new(full + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
        {
            await Forward(name, args);
            return null;
        }
        try { return new(ownership, name, route); }
        catch { ownership.Dispose(); throw; }
    }

    private static void ValidateArguments(string[] args)
    {
        if (args is null || args.Length > MaximumArguments || args.Any(arg => arg is null || arg.Length > MaximumArgumentLength))
            throw new InvalidDataException("The Team Bar launch request is too large.");
        if (JsonSerializer.SerializeToUtf8Bytes(args).Length > MaximumMessageBytes)
            throw new InvalidDataException("The Team Bar launch request is too large.");
    }

    private static async Task<string> ReadLine(Stream stream, CancellationToken token)
    {
        var bytes = new List<byte>(); byte[] one = new byte[1];
        while (bytes.Count <= MaximumMessageBytes)
        {
            if (await stream.ReadAsync(one, token).ConfigureAwait(false) == 0) throw new IOException("The Team Bar connection closed.");
            if (one[0] == 10) return Encoding.UTF8.GetString(bytes.ToArray());
            bytes.Add(one[0]);
        }
        throw new InvalidDataException("The Team Bar launch request is too large.");
    }
    private static Task WriteLine(Stream stream, string text, CancellationToken token) =>
        stream.WriteAsync(Encoding.UTF8.GetBytes(text + "\n"), token).AsTask();

    private static void Authenticate(uint pid)
    {
        using var process = Process.GetProcessById(checked((int)pid));
        if (!string.Equals(process.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Open Team Bar from the same app build as the running instance.");
    }

    private async Task Serve()
    {
        while (!stop.IsCancellationRequested)
        {
            bool failed = false, reusable = true;
            try
            {
                await server.WaitForConnectionAsync(stop.Token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out uint pid)) throw new IOException("Cannot identify the Team Bar launcher.");
                Authenticate(pid);
                var args = JsonSerializer.Deserialize<string[]>(await ReadLine(server, timeout.Token).ConfigureAwait(false))
                    ?? throw new InvalidDataException("Invalid Team Bar launch request.");
                ValidateArguments(args);
                LaunchReply reply;
                try
                {
                    await route(args).WaitAsync(timeout.Token).ConfigureAwait(false);
                    reply = new(true);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                { reply = new(false, error.Message); }
                await WriteLine(server, JsonSerializer.Serialize(reply), timeout.Token).ConfigureAwait(false);
                if (await ReadLine(server, timeout.Token).ConfigureAwait(false) != "received")
                    throw new InvalidDataException("Invalid Team Bar launch acknowledgment.");
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException
                or JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { failed = true; }
            finally { reusable = PipeServerConnection.Reset(server); }
            if (!reusable || stop.IsCancellationRequested) return;
            if (failed) await Task.Delay(50).ConfigureAwait(false);
        }
    }

    private static async Task Forward(string name, string[] args)
    {
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        // ConnectAsync waits for the owner's listener during startup. No request
        // bytes have been sent yet, so waiting here cannot duplicate a delivery.
        using (var startup = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            await client.ConnectAsync(5000, startup.Token).ConfigureAwait(false);
        if (!GetNamedPipeServerProcessId(client.SafePipeHandle, out uint pid)) throw new IOException("Cannot identify the running Team Bar app.");
        Authenticate(pid);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await WriteLine(client, JsonSerializer.Serialize(args), timeout.Token).ConfigureAwait(false);
        var reply = JsonSerializer.Deserialize<LaunchReply>(await ReadLine(client, timeout.Token).ConfigureAwait(false))
            ?? throw new IOException("Invalid Team Bar launch response.");
        await WriteLine(client, "received", timeout.Token).ConfigureAwait(false);
        // Retain this connection until the owner finishes the prior transaction.
        // Never resend: routing may already have happened if the transport fails.
        try
        {
            if (await client.ReadAsync(new byte[1], timeout.Token).ConfigureAwait(false) != 0)
                throw new IOException("Unexpected data after the Team Bar launch response.");
        }
        catch (IOException error) when ((error.HResult & 0xffff) is 109 or 232) { }
        if (!reply.Ok) throw new InvalidOperationException(reply.Error ?? "The running Team Bar app could not open that window.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel(); server.Dispose(); ownership.Dispose();
        _ = serving.ContinueWith(_ => stop.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    [DllImport("kernel32.dll")] private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll")] private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);
}
