using System.IO.Pipes;
using System.Text;
using System.Text.Json;
namespace TeamBar;

internal static class PipeDisconnectTests
{
    // Private fixture endpoints only: no app settings, game handles or input.
    internal static async Task Abandon(string name, string request, bool afterReply, CancellationToken token)
    {
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(2000, token);
        await client.WriteAsync(Encoding.UTF8.GetBytes(request + (afterReply ? "\n" : "")), token);
        if (afterReply)
        {
            using var reader = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true);
            if (await reader.ReadLineAsync(token) is null) throw new IOException("Fixture server did not reply.");
        }
    }

    internal static void Run(string output)
    {
        var checks = new List<string>();
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); checks.Add(message); }
        try
        {
            SpellOverlayInputPipeTests.Run(Check);
            Task.Run(() => OtherEndpoints(output, Check)).GetAwaiter().GetResult();
            CollectorFormat.WriteNew(output, new { passed = true, tests = checks, gameInputSent = false, processMemoryRead = false });
        }
        catch (Exception error)
        {
            CollectorFormat.WriteNew(output, new { passed = false, tests = checks, error = error.ToString(), gameInputSent = false, processMemoryRead = false });
            throw;
        }
    }

    private static async Task OtherEndpoints(string output, Action<bool, string> check)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        string profile = Path.GetFullPath(output) + ".bar";
        int cleared = 0, closed = 0;
        var handling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var bar = new BarSession(profile, () => true, () => closed++, async () =>
        {
            cleared++; handling.TrySetResult(); await resume.Task.WaitAsync(deadline.Token);
        }))
        {
            await BreakReply(BarSession.Name(profile), "clear-hotkeys", handling, resume, deadline.Token);
            var status = await BarSession.Request(profile, "status").WaitAsync(deadline.Token);
            check(status is { Ready: true } && status.ProcessId == Environment.ProcessId,
                "bar control pipe recovers after writing to a disconnected peer");
            check(cleared == 1 && closed == 0, "bar recovery neither replays clear nor closes the host");
        }

        string scope = Path.GetFullPath(output) + ".instance";
        int routed = 0;
        handling = new(TaskCreationOptions.RunContinuationsAsynchronously);
        resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var app = await AppInstanceCoordinator.StartOrForward(scope, [], async args =>
        {
            routed++;
            if (args.SequenceEqual(new[] { "disconnect-before-reply" }))
            { handling.TrySetResult(); await resume.Task.WaitAsync(deadline.Token); }
        }))
        {
            await BreakReply(AppInstanceCoordinator.Name(scope), "[\"disconnect-before-reply\"]", handling, resume, deadline.Token);
            using var forwarded = await AppInstanceCoordinator.StartOrForward(scope, ["--items"],
                _ => throw new InvalidOperationException("The recovery client must not become the owner.")).WaitAsync(deadline.Token);
            check(app is not null && forwarded is null && routed == 2,
                "app launcher recovers a broken reply and routes the next request exactly once");
        }
    }

    internal static async Task BreakReply(string name, string request, TaskCompletionSource handling,
        TaskCompletionSource resume, CancellationToken token)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(2000, token);
            await client.WriteAsync(Encoding.UTF8.GetBytes(request + "\n"), token);
            await handling.Task.WaitAsync(token);
        }
        finally { resume.TrySetResult(); } // Dispose the client before allowing the server's reply.
    }
}
