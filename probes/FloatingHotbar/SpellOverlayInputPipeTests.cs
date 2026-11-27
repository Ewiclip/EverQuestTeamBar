using System.IO.Pipes;
using System.Text;
using System.Text.Json;
namespace TeamBar;

internal static class SpellOverlayInputPipeTests
{
    internal static void Run(Action<bool, string> check) =>
        Task.Run(() => RunAsync(check)).GetAwaiter().GetResult();

    private static async Task RunAsync(Action<bool, string> check)
    {
        var legacyReply = JsonSerializer.Deserialize<SpellClickReply>("{\"Ok\":false,\"Message\":\"Old reply\",\"Paused\":true}");
        check(legacyReply is { RefreshCharactersRequired: false, ReconnectRequired: false },
            "older spell replies default to no character-refresh request");
        // These private endpoints exchange status messages in this process only.
        // Running off the UI thread keeps the fixture independent of GUI pumps.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        string name = "TeamBar-SpellPipeBarrierTest-" + Guid.NewGuid().ToString("N");
        using (var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var disconnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task Serve()
            {
                await server.WaitForConnectionAsync(deadline.Token);
                using (var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true))
                using (var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\n" })
                {
                    var request = JsonSerializer.Deserialize<SpellClickRequest>(await reader.ReadLineAsync(deadline.Token) ?? "null");
                    if (request?.Kind != "status") throw new InvalidDataException("Expected a status fixture request.");
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new SpellClickReply(true, "Fixture ready", false, "fixture-session")).AsMemory(), deadline.Token);
                    if (await reader.ReadLineAsync(deadline.Token) != "received") throw new InvalidDataException("Expected a received acknowledgment.");
                }
                acknowledged.SetResult();
                await disconnect.Task.WaitAsync(deadline.Token);
                server.Disconnect();
            }
            var serving = Serve();
            var pending = SpellOverlayInputPipe.RequestNamed(new("status"), name);
            try
            {
                await acknowledged.Task.WaitAsync(deadline.Token);
                // The server is deliberately held after receipt; merely writing
                // the acknowledgment must not complete and release the client gate.
                var first = await Task.WhenAny(pending, Task.Delay(150, deadline.Token));
                check(first != pending && !pending.IsCompleted,
                    "spell pipe request waits for server disconnect after its received acknowledgment");
            }
            finally { disconnect.TrySetResult(); }
            var reply = await pending.WaitAsync(deadline.Token);
            await serving.WaitAsync(deadline.Token);
            check(reply is { Ok: true, Paused: false, Session: "fixture-session" },
                "spell pipe returns its reply after the server disconnect barrier");
        }

        name = "TeamBar-SpellPipeReconnectTest-" + Guid.NewGuid().ToString("N");
        int handled = 0;
        var handling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var endpoint = new SpellOverlayInputPipe(async (request, pid) =>
        {
            Interlocked.Increment(ref handled);
            if (request.Kind == "disconnect-before-reply")
            {
                handling.TrySetResult();
                await resumeReply.Task.WaitAsync(deadline.Token);
            }
            return new SpellClickReply(request.Kind == "status" && pid == Environment.ProcessId, "Fixture ready", false,
                RefreshCharactersRequired: request.Kind == "stale-roster");
        }, name);
        string? session = null;
        for (int i = 0; i < 64; i++)
        {
            var reply = await SpellOverlayInputPipe.RequestNamed(new("status"), name).WaitAsync(deadline.Token);
            session ??= reply?.Session;
            check(reply is { Ok: true, Paused: false, Session: not null } && reply.Session == session,
                "rapid spell pipe status request reconnects to the authenticated session: " + i);
        }
        check(handled == 64, "rapid spell pipe reconnects handle each request exactly once without retries");
        int beforeRoster = handled;
        var staleRoster = await SpellOverlayInputPipe.RequestNamed(new("stale-roster"), name).WaitAsync(deadline.Token);
        check(staleRoster is { Ok: false, RefreshCharactersRequired: true, ReconnectRequired: false } && staleRoster.Session == session
            && handled == beforeRoster + 1, "typed roster-refresh rejection crosses the authenticated pipe exactly once without action retry");

        // A peer can vanish during a request or before acknowledging its reply.
        // Both paths leave a .NET pipe Broken (IsConnected == false), not reusable.
        foreach (bool afterReply in new[] { false, true })
        {
            int before = handled;
            await PipeDisconnectTests.Abandon(name, JsonSerializer.Serialize(new SpellClickRequest("status")), afterReply, deadline.Token);
            var reply = await SpellOverlayInputPipe.RequestNamed(new("status"), name).WaitAsync(deadline.Token);
            check(reply is { Ok: true } && reply.Session == session,
                "spell pipe recovers after peer closes " + (afterReply ? "before acknowledgment" : "mid-request"));
            check(handled == before + (afterReply ? 2 : 1), "abandoned spell request is never replayed");
        }

        int beforeBrokenWrite = handled;
        // Force the reply write to happen after the peer is gone, not merely race
        // a successful reply against Dispose. This is the observed Broken state.
        await PipeDisconnectTests.BreakReply(name, JsonSerializer.Serialize(new SpellClickRequest("disconnect-before-reply")), handling, resumeReply, deadline.Token);
        var recovered = await SpellOverlayInputPipe.RequestNamed(new("status"), name).WaitAsync(deadline.Token);
        check(recovered is { Ok: true } && recovered.Session == session, "spell pipe recovers after writing to a disconnected peer");
        check(handled == beforeBrokenWrite + 2, "failed reply does not replay its handled request");
    }
}
