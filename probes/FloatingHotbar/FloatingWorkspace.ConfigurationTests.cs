using TeamBar;

internal sealed partial class FloatingWorkspace
{
    internal void ConfigurationSelfTest(string profile, Action<bool, string> check)
    {
        var oldTeam = live.Team; var oldButtons = appliedButtons;
        var edited = appliedButtons.Select(b => b with { Value = b.Value with { Label = "Updated without apply",
            Rules = [new(new(CharacterId: "fixture"), "E"), new(new(CharacterId: "fixture"), "Alt+E"), new(new(CharacterId: "fixture"), "Ctrl+Shift+H")] } }).ToArray();
        busy = true; QueueConfiguration(profile, oldTeam, edited);
        check(pendingConfiguration is not null && appliedButtons == oldButtons, "Live updates wait until an in-progress click finishes.");
        check(RecoverySnapshot().Buttons[0].Value.Label == "Updated without apply", "A disconnect retains an already-saved edit even before the old click finishes.");
        var expected = live.Identities; var observed = current;
        current = []; identityBlocked = true; busy = false; ApplyPendingConfiguration();
        check(pendingConfiguration is not null && live.Identities == expected,
            "Autosave cannot replace the bound roster with a partial mismatched identity observation.");
        identityBlocked = false; identityUnavailable = true; ApplyPendingConfiguration();
        check(pendingConfiguration is not null && live.Identities == expected,
            "Autosave waits for all clients to finish loading before updating live routes.");
        identityUnavailable = false; current = observed;
        busy = false; ApplyPendingConfiguration();
        var single = surfaces.Single(s => s.Condition is not null);
        check(pendingConfiguration is null && single.Actions.Single().Label == "Updated without apply"
            && single.Taps.Single().Select(t => t.Key).SequenceEqual(["E", "Alt+E", "Ctrl+Shift+H"]),
            "Saved labels and reused-key chords become live without reconnecting or dispatching input.");
        QueueConfiguration(profile, oldTeam with { Buttons = [], Groups = [new("test-group", "Shared group", [new(edited[0].Value.Id, 1)], 2)] },
            edited.Select(b => b with { Value = b.Value with { Floating = false } }).ToArray());
        check(surfaces.Count == 1 && surfaces[0].Group is not null && surfaces[0].Members.Single().Column == 1,
            "A group-only button and its explicit column update live.");
        QueueConfiguration(profile, oldTeam, oldButtons);
    }
}
