using System.Windows.Interop;
using W = System.Windows;
using C = System.Windows.Controls;
namespace TeamBar;

internal sealed partial class SpellOverlayWindow
{
    private readonly List<(C.Border Border, SpellWatch Parent)> clickRows = [];
    private MouseShield? clickShield;
    private bool clickConnected, clickBusy, clickPaused = true;
    private long clickLeaseUntil;
    private SpellClickRequest? clickContext, dockClickContext;
    private Func<SpellClickRequest, Task<SpellClickReply?>>? sendClick;
    private (C.Border Border, W.Rect Bounds, SpellClickRequest Request)? pressedSpell;
    private string clickMessage = "Connecting spell buttons…";
    private bool HasClickMappings => currentDefinition?.HasClickMappings == true;
    private bool ClickSurface => !editingButtons && !CanEdit && clickConnected && HasClickMappings;
    internal void SetClickContext(TargetCapture? capture)
    {
        var next = capture?.Target is { } target && MyTargetSpells.Matches(capture) ? new SpellClickRequest("tap",
            ProcessId: capture.ProcessId, Started: capture.ProcessStartedUtcTicks, TargetId: target.SpawnId,
            TargetName: target.Name, Class: capture.CharacterClass, ZoneId: capture.Zone.Id) : null;
        if (next != clickContext) CancelSpellClick();
        clickContext = next;
        var dockNext = capture is null ? null : new SpellClickRequest("dock-tap", ProcessId: capture.ProcessId, Started: capture.ProcessStartedUtcTicks,
            TargetId: capture.Target?.SpawnId ?? 0, TargetName: capture.Target?.Name, Class: capture.CharacterClass, ZoneId: capture.Zone.Id);
        if (dockNext != dockClickContext) CancelSpellClick();
        dockClickContext = dockNext;
    }
    internal void SetClickConnection(bool connected, bool paused, string message, Func<SpellClickRequest, Task<SpellClickReply?>> sender)
    {
        clickConnected = connected; clickPaused = paused; clickMessage = message; sendClick = sender;
        clickLeaseUntil = connected ? Environment.TickCount64 + 2000 : 0;
        if (!connected || paused) CancelSpellClick();
        UpdateInputMode(); UpdateClickHint();
    }
    private void UpdateClickHint()
    {
        if (!HasClickMappings && !editingButtons) return;
        string value = editingButtons ? buttonEditError ?? "Editing buttons" : CanEdit ? "Editing layout · casting disabled" : clickMessage;
        // Fixed one-line footer: status updates must not resize the overlay.
        hint.TextWrapping = W.TextWrapping.NoWrap; hint.TextTrimming = W.TextTrimming.CharacterEllipsis;
        hint.Text = value; hint.ToolTip = value;
        foreach (var row in clickRows) row.Border.Cursor = editingButtons ? System.Windows.Input.Cursors.Arrow : CanEdit ? System.Windows.Input.Cursors.SizeAll
            : row.Parent.ClickKey is not null ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow;
    }
    private void UpdateClickShield()
    {
        if (!ClickSurface)
        {
            CancelSpellClick(); clickShield?.Dispose(); clickShield = null; return;
        }
        if (clickShield is null)
        {
            nint handle = new WindowInteropHelper(this).Handle;
            if (handle != 0) clickShield = new(handle);
        }
    }
    private W.Rect RowBounds(C.Border border) => border.TransformToAncestor(this).TransformBounds(new(0, 0, border.ActualWidth, border.ActualHeight));
    private void CancelSpellClick()
    {
        if (pressedSpell is { } pressed) pressed.Border.Opacity = 1;
        pressedSpell = null;
    }
    private async void RoutedSpellMouse(int message, System.Drawing.Point screen)
    {
        if (message is not (0x201 or 0x202)) return;
        try
        {
            if (!ClickSurface || clickPaused || clickBusy || clickShield?.PostFault != false
                || Environment.TickCount64 >= clickLeaseUntil) { CancelSpellClick(); return; }
            var point = PointFromScreen(new(screen.X, screen.Y));
            var row = clickRows.FirstOrDefault(r => r.Parent.ClickKey is not null && RowBounds(r.Border).Contains(point));
            var dock = dockRows.FirstOrDefault(r => r.Button.Available && RowBounds(r.Border).Contains(point));
            C.Border? hit = dock.Border ?? row.Border;
            if (message == 0x201)
            {
                CancelSpellClick();
                if (hit is null || currentDefinition is null) return;
                var context = dock.Border is not null ? dockClickContext : clickContext;
                if (context is null) return;
                var request = context with { Window = new WindowInteropHelper(this).Handle.ToInt64(), OverlayId = currentDefinition.Id,
                    Foreground = Native.GetForegroundWindow().ToInt64() };
                request = dock.Border is not null ? request with { GroupId = dock.Group, ButtonId = dock.Button.Id, ButtonRevision = dock.Button.Revision, Recipients = dock.Button.Recipients }
                    : request with { SpellId = row.Parent.SpellId, Target = row.Parent.Target, Key = row.Parent.ClickKey };
                pressedSpell = (hit, RowBounds(hit), request); hit.Opacity = .7;
                return;
            }
            var pressed = pressedSpell; CancelSpellClick();
            if (pressed is null || hit != pressed.Value.Border || RowBounds(hit!) != pressed.Value.Bounds
                || Native.GetForegroundWindow().ToInt64() != pressed.Value.Request.Foreground || sendClick is null) return;
            clickBusy = true;
            try
            {
                var reply = await sendClick(pressed.Value.Request);
                clickMessage = reply?.Message ?? "Reconnecting spell buttons…";
                if (reply is null || reply.ReconnectRequired || reply.RefreshCharactersRequired) { clickConnected = false; CancelSpellClick(); }
            }
            finally { clickBusy = false; UpdateInputMode(); UpdateClickHint(); }
        }
        catch (Exception error) { clickMessage = error.Message; clickConnected = false; CancelSpellClick(); UpdateInputMode(); UpdateClickHint(); }
    }
}
