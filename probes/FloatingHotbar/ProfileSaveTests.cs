internal static class ProfileSaveTests
{
    internal static void Run(string path, TeamDefinition team, ClientBinding[] clients)
    {
        byte[] original = TeamEditing.SaveChecked(path, team, clients, null);
        using var ready = new CountdownEvent(2); using var start = new ManualResetEventSlim();
        Task<byte[]?> Save(string label) => Task.Run(() =>
        {
            ready.Signal(); start.Wait();
            try { return TeamEditing.SaveChecked(path, team with { Buttons = [team.Buttons[0] with { Label = label }] }, clients, original); }
            catch (IOException) { return null; }
        });
        var first = Save("First editor"); var second = Save("Second editor");
        bool prepared = ready.Wait(5000); start.Set();
        Task.WaitAll(first, second);
        StandaloneTests.Check(prepared && (first.Result is null) != (second.Result is null), "Two concurrent editors must not both replace the same profile revision.");
        byte[] winner = first.Result ?? second.Result!;
        StandaloneTests.Check(winner.SequenceEqual(File.ReadAllBytes(path)), "Save did not return its exact persisted revision.");
        bool refused = false;
        try { TeamEditing.SaveChecked(path, team, clients, original); } catch (IOException) { refused = true; }
        StandaloneTests.Check(refused && winner.SequenceEqual(File.ReadAllBytes(path)), "A stale editor overwrote the winning revision.");
        Routing.Validate(TeamEditing.Load(path), clients);
    }
}
