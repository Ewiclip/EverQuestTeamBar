internal sealed partial class FloatingWorkspace
{
    private Queue<RoutedTap[]> remainingTaps = new();
    private bool sequenceUsesSpellChannel;

    private void BeginTapSequence(RoutedTap[] taps, bool spellChannel = false)
    {
        remainingTaps = TapSequence.Create(taps);
        sequenceUsesSpellChannel = spellChannel;
        SendNextTap();
    }

    private void SendNextTap()
    {
        var batch = remainingTaps.Dequeue();
        pendingSince = TapClock();
        if (sequenceUsesSpellChannel) backend!.SpellTap(++sequence, batch.Single());
        else backend!.Tap(++sequence, batch);
        Write("sequence_step", new { id = sequence, remaining = remainingTaps.Count, targets = batch });
    }
}
