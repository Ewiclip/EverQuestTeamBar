using System.Text.Json;

internal sealed partial class FloatingWorkspace
{
    internal void SequenceSelfTest(string scenario)
    {
        static void Check(bool value, string message) => StandaloneTests.Check(value, message);
        Start(); EditionWait(() => Ready);
        var first = surfaces[0].Taps[0][0];
        var taps = new[] { first with { Key = "Alt+6", Scan = InputKeys.Scan("6"), Modifiers = 4 },
            first with { Key = "Ctrl+Shift+7", Scan = InputKeys.Scan("7"), Modifiers = 3 },
            first with { Key = "Ctrl+Shift+7", Scan = InputKeys.Scan("7"), Modifiers = 3 } };
        busy = true; pendingSpellTap = scenario == "dock";
        BeginTapSequence(taps);
        Check(sequence == 1 && busy && remainingTaps.Count == 2, "The first step dispatched more than one key to a character.");
        var initial = sequence;
        Receive(JsonSerializer.SerializeToElement(new { type = "complete", id = initial + 100, ok = true }));
        Check(sequence == initial && remainingTaps.Count == 2, "An unrelated completion advanced the sequence.");
        if (scenario is "bar" or "dock")
        {
            EditionWait(() => !busy);
            Check(sequence == 3 && remainingTaps.Count == 0 && !Failed, "An ordered modifier sequence did not finish exactly once.");
            for (int i = 0; i < 5; i++) { CheckDelivery(); CheckIdentities(); }
            Check(sequence == 3, "A completed sequence repeated without another click.");
        }
        else if (scenario.StartsWith("reject-", StringComparison.Ordinal))
        {
            string reason = scenario switch { "reject-key" => "Release the mapped key first",
                "reject-modifiers" => "Release modifier keys first", _ => "A client is not reading input" };
            Receive(JsonSerializer.SerializeToElement(new { type = "rejected", id = initial, reason }));
            Check(Ready && Visible && !Failed && !busy && remainingTaps.Count == 0,
                "An ordinary preflight rejection closed floating controls or retained queued keys.");
            Application.DoEvents(); Check(sequence == 1 && !busy, "A rejected click was automatically replayed.");
            busy = true; BeginTapSequence(taps); EditionWait(() => !busy);
            Check(sequence == 4 && !Failed, "A fresh click after a preflight rejection could not complete.");
        }
        else if (scenario == "cancel")
        {
            CancelPendingInput("Sequence cancellation fixture");
            EditionWait(() => !busy && !cancelInFlight);
            Check(sequence == 1 && remainingTaps.Count == 0 && !Failed, "Cancellation allowed a queued key to run.");
        }
        else if (scenario == "persona")
        {
            ReadIdentity = expected => expected with { Character = new("Runa", 2, 130) };
            EditionWait(() => !busy && !cancelInFlight);
            Check(sequence == 1 && remainingTaps.Count == 0 && !Failed, "A persona transition sent keys intended for the previous class.");
        }
        else if (scenario == "failure")
        {
            Receive(JsonSerializer.SerializeToElement(new { type = "complete", id = initial, ok = false }));
            Check(Failed && remainingTaps.Count == 0 && sequence == 1, "A failed step did not discard the rest.");
        }
        else if (scenario == "timeout")
        {
            TapClock = () => pendingSince + 3001; CheckDelivery();
            Check(Failed && remainingTaps.Count == 0 && sequence == 1, "An unacknowledged step did not discard the rest.");
        }
        else throw new InvalidOperationException("Unknown sequence fixture.");
        Write("sequence_test_pass", new { scenario, gameInput = false }); Close();
    }
}
