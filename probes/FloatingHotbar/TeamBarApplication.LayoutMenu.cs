using F = System.Windows.Forms;
namespace TeamBar;

internal sealed partial class TeamBarApplication
{
    private readonly F.ToolStripMenuItem layoutMenu = new("Window layouts");
    private readonly F.ToolStripSeparator layoutSeparator = new();
    private string? activeLayoutProfile;

    internal bool IsActiveGameView(string file, string layoutName) =>
        string.Equals(file, activeLayoutProfile, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(layoutName, ActiveGameViewName, StringComparison.OrdinalIgnoreCase);

    private void PopulateLayoutMenu()
    {
        // Keep the fixed commands and only replace the names read for this profile.
        while (layoutMenu.DropDownItems.IndexOf(layoutSeparator) > 0)
        {
            var old = layoutMenu.DropDownItems[0];
            layoutMenu.DropDownItems.RemoveAt(0);
            old.Dispose();
        }
        int index = 0;
        try
        {
            string file = profile ?? DefaultProfile();
            foreach (var layout in GameViewLayouts.Load(file))
            {
                var item = new F.ToolStripMenuItem(layout.Name.Replace("&", "&&"))
                {
                    Checked = IsActiveGameView(file, layout.Name)
                };
                item.Click += (_, _) => OpenSafely(() =>
                {
                    var setup = EnsureSetup(file, controller ?? SpellOverlayInputHost.ControllerPath());
                    setup.ApplyTrayLayout(this, layout);
                });
                layoutMenu.DropDownItems.Insert(index++, item);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            layoutMenu.DropDownItems.Insert(index++, new F.ToolStripMenuItem("Saved layouts unavailable")
                { Enabled = false, ToolTipText = error.Message });
        }
        layoutSeparator.Visible = index > 0;
    }

    private void EditLayouts()
    {
        ShowDefaultSetup();
        setups[profile!].ShowScreenLayoutEditor();
    }

    internal void ApplyProfileLayout(string file, GameViewLayout layout, Func<GameSession[]> listWindows,
        Func<Discovery> roster, Action? beforeApply = null, bool keepFocus = false)
    {
        bool activePreset = IsActiveGameView(file, layout.Name);
        var slots = GameViewSession.DisplaySlots(layout);
        if (keepFocus && activePreset && beforeApply is null && activeGameView is { } view &&
            view.Layout.Characters.SequenceEqual(layout.Characters, StringComparer.OrdinalIgnoreCase) &&
            (view.Layout.Windows ?? []).SequenceEqual(layout.Windows ?? []))
        {
            // Keep the known occupants while EQ is zoning. Roster polling handles
            // subsequent logins and character changes after this presentation edit.
            var occupied = view.Clients.Select(c => c is not null && GameViewWindows.Available((nint)c.Window, c.ProcessId) ? c : null).ToArray();
            ApplyGameView(file, layout, slots, occupied, view.Refresh, keepFocus: true);
            return;
        }
        var cache = new GameViewRosterCache(layout);
        GameSession?[] Resolve(GameSession?[] previous) => cache.ResolveOpenWindows(listWindows(), roster, previous);
        var seats = Resolve(PreviousLayoutSeats(layout));
        beforeApply?.Invoke();
        ApplyGameView(file, layout, slots, seats, Resolve, keepFocus);
    }
}
