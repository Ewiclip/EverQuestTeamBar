namespace TeamBar;

internal sealed partial class GameViewSession
{
    // A widget belongs to a physical slot. Capture its current occupant so a
    // delayed click from a replaced widget can never select the new character.
    private sealed class SlotButton : IDisposable
    {
        internal readonly Client Client;
        internal readonly GameViewSwapButton Widget;
        internal readonly int Slot;
        internal SlotButton(GameViewSession session, int slot, Client client)
        {
            Slot = slot;
            Client = client;
            Widget = new(() => session.SelectSwapButton(this), point => session.MoveSwapButton(this, point), session.swapButtonTheme);
        }
        public void Dispose() => Widget.Dispose();
    }

    private readonly Dictionary<int, SlotButton> swapButtons = [];
    private FloatingTheme swapButtonTheme = FloatingThemes.ForAppTheme(AppearanceTheme.Dark);
    internal Func<GameViewLayout, int, RelativePosition?>? ReadSwapButtonPosition { get; set; }
    internal Action<GameViewLayout, int, RelativePosition>? SaveSwapButtonPosition { get; set; }

    internal void SetSwapButtonTheme(FloatingTheme theme)
    {
        if (swapButtonTheme == theme) return;
        swapButtonTheme = theme;
        foreach (var button in swapButtons.Values) button.Widget.SetTheme(theme);
    }

    private void LoadSwapButtonPositions(Plan next, Plan? previous)
    {
        if (!next.Layout.AddSwapButton) return;
        next.PositionReader = ReadSwapButtonPosition;
        // Validate the new profile/slot anchors before touching either window.
        // Reuse the verified anchors on swaps, roster refreshes and rollback.
        next.ButtonPositions = previous is not null && ReferenceEquals(previous.Layout, next.Layout) &&
            previous.PositionReader == ReadSwapButtonPosition
            ? previous.ButtonPositions
            : Enumerable.Range(0, next.Slots.Length).Select(i => ReadSwapButtonPosition?.Invoke(next.Layout, i)).ToArray();
    }

    private void SyncSwapButtons(Plan view, GameViewTile?[] tiles)
    {
        if (!view.Layout.AddSwapButton || view.Layout.SwapTargetIndex < 0)
        {
            ClearSwapButtons();
            return;
        }
        foreach (var (slot, button) in swapButtons.ToArray())
            if (slot >= view.Slots.Length || view.At(slot) != button.Client)
            {
                swapButtons.Remove(slot);
                button.Dispose();
            }
        for (int slot = 0; slot < view.Slots.Length; slot++)
        {
            if (view.At(slot) is not { } client) continue;
            if (!swapButtons.TryGetValue(slot, out var button))
            {
                button = new(this, slot, client);
                swapButtons.Add(slot, button);
            }
            var area = view.Slots[slot];
            if (tiles[slot] is { } tile && tile.TryGetDisplayedClient(out var displayed))
                area = Rectangle.Intersect(area, displayed);
            else if (tiles[slot] is null)
                area = Rectangle.Intersect(area, GameViewPlacements.ClientBounds(client.Handle, client.Pid));
            if (area.Width <= 0 || area.Height <= 0)
            {
                button.Widget.Relock();
                button.Widget.Hide();
                continue;
            }
            string? text = view.Layout.SwapButtonStyle == GameViewSwapButtonStyle.Icon ? null :
                view.Layout.SwapButtonLabel(client.Name);
            button.Widget.Present(area, client.Name, text, view.ButtonPositions[slot]);
        }
    }

    private void MoveSwapButton(SlotButton button, RelativePosition point)
    {
        if (ended || updating || plan is not { } current || swapButtons.GetValueOrDefault(button.Slot) != button) return;
        try
        {
            SaveSwapButtonPosition?.Invoke(current.Layout, button.Slot, point);
            current.ButtonPositions[button.Slot] = point;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetPreviewNotice($"Could not save the swap button position: {error.Message}");
        }
    }

    private void SelectSwapButton(SlotButton button)
    {
        if (ended || updating || WaitingForGameDialog || swapButtons.GetValueOrDefault(button.Slot) != button) return;
        CheckWindows();
        var client = button.Client;
        int index = button.Slot;
        if (ended || plan is not { } current || !current.Layout.AddSwapButton || swapButtons.GetValueOrDefault(index) != button ||
            current.At(index) != client || !GameViewWindows.Available(client.Handle, client.Pid)) return;
        try
        {
            // Selecting the current master is a focus action and never triggers a resize.
            bool focused = index == current.Layout.SwapTargetIndex
                ? GameViewWindows.TryFocus(client.Handle) : Promote(index);
            SetPreviewNotice(focused ? null : $"Windows declined to focus {client.Name}.");
        }
        catch (InvalidOperationException error) when (!ended) { SetPreviewNotice(error.Message); }
    }

    private void RelockSwapButtons()
    {
        foreach (var button in swapButtons.Values) button.Widget.Relock();
    }

    private void ClearSwapButtons()
    {
        foreach (var button in swapButtons.Values) button.Dispose();
        swapButtons.Clear();
    }

#if !TEAMBAR_PUBLIC_RELEASE
    internal GameViewSwapButton? SwapButtonAt(int slot) => swapButtons.GetValueOrDefault(slot)?.Widget;
    internal Action CaptureSwapButtonClickForTest(int slot)
    {
        var button = swapButtons[slot];
        return () => SelectSwapButton(button);
    }
#endif
}
