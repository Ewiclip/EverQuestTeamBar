namespace TeamBar;

internal sealed class MonitorIdentification : IDisposable
{
    private readonly List<IdentificationBadge> badges = [];
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 8000 };

    // Use the same display identifier on the map and on the physical monitor,
    // even when the selected monitors are nonconsecutive or in a different order.
    internal static string Label(string device)
    {
        var name = device.TrimEnd('\0').Split('\\').Last();
        const string prefix = "DISPLAY";
        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(name.AsSpan(prefix.Length), out int number) && number > 0)
            return $"Screen {number}";
        return name;
    }

    internal MonitorIdentification(IEnumerable<GameViewMonitor> displays, Action<string> report)
    {
        try
        {
            foreach (var display in displays)
            {
                var bounds = display.Bounds;
                if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) continue;
                var heading = Label(display.Device);
                string marker = heading.StartsWith("Screen ", StringComparison.Ordinal) ? heading[7..] : "▣";
                string caption = $"{bounds.Width} × {bounds.Height}";
                var badge = new IdentificationBadge(marker, heading, caption);
                badges.Add(badge);
                // Create the handle on its destination monitor for the correct DPI.
                badge.Location = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
                _ = badge.Handle;
                var size = badge.BadgeSize;
                badge.Place(new Rectangle(bounds.Left + (bounds.Width - size.Width) / 2,
                    bounds.Top + (bounds.Height - size.Height) / 2, size.Width, size.Height));
            }
            if (badges.Count == 0) { report("No connected screens to identify."); Dispose(); return; }
            timer.Tick += (_, _) => Dispose();
            timer.Start();
            report("Screen numbers appear on the connected monitors for eight seconds.");
        }
        catch { Dispose(); throw; }
    }

    public void Dispose()
    {
        timer.Dispose();
        foreach (var badge in badges) badge.Dispose();
        badges.Clear();
    }
}
