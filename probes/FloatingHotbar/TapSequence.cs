// One finite sequence per explicit click. Each batch has at most one tap per
// character. A caller must wait for successful release acknowledgments before
// taking the next batch, and discard the queue on cancellation or failure.
internal static class TapSequence
{
    internal static Queue<RoutedTap[]> Create(RoutedTap[] taps)
    {
        var recipients = taps.GroupBy(t => t.ProcessId).Select(g => g.ToArray()).ToArray();
        if (recipients.Length is < 1 or > 6 || recipients.Any(r => r.Length > 24))
            throw new InvalidOperationException("Choose one to six characters and up to 24 keypresses per character.");
        return new(Enumerable.Range(0, recipients.Max(r => r.Length))
            .Select(step => recipients.Where(r => r.Length > step).Select(r => r[step]).ToArray()));
    }
}
