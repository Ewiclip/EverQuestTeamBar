using System.Windows.Interop;
using W = System.Windows;
using C = System.Windows.Controls;
using I = System.Windows.Input;
namespace TeamBar;

internal sealed partial class SpellOverlayWindow
{
    private readonly Func<SpellOverlayDefinition, SpellOverlayLayoutChange, SpellOverlayDefinition>? saveLayout;
    private readonly SpellOverlayLockWindow? layoutLock;
    private readonly C.Border resizeGrip;
    private SpellOverlayDefinition? currentDefinition;
    private SpellOverlayState? currentState;
    private GameFrame? currentFrame;
    private System.Drawing.Rectangle wholeBounds;
    private OverlayDesktopPosition? freeSeed;
    internal OverlayDesktopPosition? DesktopPosition => IsVisible && wholeBounds.Width > 0 ? new(wholeBounds.X, wholeBounds.Y) : null;
    internal Func<System.Drawing.Rectangle[]> ReadDesktopAreas = () => System.Windows.Forms.Screen.AllScreens.Select(s => s.WorkingArea).ToArray();
    private sealed record LayoutEdit(SpellOverlayDefinition Original, SpellOverlayDefinition Preview, GameFrame Frame,
        System.Drawing.Point Cursor, System.Drawing.Rectangle Bounds, bool Resize, double VisibleScale, string? GroupId = null, W.Size? GroupSize = null);
    private LayoutEdit? edit;
    private string? layoutError;
    internal SpellOverlayLockWindow? LayoutLock => layoutLock;
    internal Func<long> LayoutClock { get; set; } = () => Environment.TickCount64;
    private readonly System.Windows.Threading.DispatcherTimer autoLockTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private long lastLayoutMovement;
    private bool? observedLocked;
    private bool forcedLocked;
    private bool CanEdit => !editingButtons && saveLayout is not null && currentDefinition is { Locked: false } && !forcedLocked;
    private SpellOverlayDefinition EffectiveDefinition(SpellOverlayDefinition definition) => forcedLocked ? definition with { Locked = true } : definition;
    private void InitializeLayoutInput()
    {
        autoLockTimer.Tick += (_, _) => CheckAutoRelock();
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (!CanEdit || !GetCursorPos(out var cursor) || !BeginLayoutEdit(resizeGrip.IsMouseOver, cursor, ResizingDock)) return;
            if (!CaptureMouse()) EndLayoutEdit(true);
            e.Handled = true;
        };
        PreviewMouseMove += (_, e) =>
        {
            if (edit is null) return;
            if (e.LeftButton != I.MouseButtonState.Pressed) EndLayoutEdit(false);
            else if (GetCursorPos(out var cursor)) UpdateLayoutEdit(cursor);
            e.Handled = true;
        };
        PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (edit is null) return;
            if (GetCursorPos(out var cursor)) UpdateLayoutEdit(cursor);
            EndLayoutEdit(false); e.Handled = true;
        };
        LostMouseCapture += (_, _) => { if (edit is not null) EndLayoutEdit(true); };
        Deactivated += (_, _) => { if (edit is not null) EndLayoutEdit(true); };
        PreviewKeyDown += (_, e) => { if (e.Key == I.Key.Escape && edit is not null) { EndLayoutEdit(true); e.Handled = true; } };
    }
    private void UpdateInputMode()
    {
        UpdateClickShield();
        IsHitTestVisible = editingButtons || CanEdit || ClickSurface; Cursor = CanEdit ? I.Cursors.SizeAll : I.Cursors.Arrow;
        resizeGrip.Visibility = CanEdit ? W.Visibility.Visible : W.Visibility.Collapsed;
        nint handle = new WindowInteropHelper(this).Handle;
        if (handle == 0) return;
        long style = GetWindowLongPtrW(handle, -20).ToInt64();
        long desired = (editingButtons || CanEdit ? style & ~0x08000020L : ClickSurface ? (style | 0x08000000L) & ~0x20L : style | 0x08000020L) | 0x80;
        if (desired != style) SetWindowLongPtrW(handle, -20, (nint)desired);
    }
    private void ObserveLockState()
    {
        if (!CanEdit) { observedLocked = true; autoLockTimer.Stop(); return; }
        if (observedLocked != false) { observedLocked = false; lastLayoutMovement = LayoutClock(); autoLockTimer.Start(); }
    }
    internal void CheckAutoRelock()
    {
        if (!CanEdit || edit is not null || LayoutClock() - lastLayoutMovement < 15000) return;
        // Do not leave a movable overlay behind if persisting the timeout lock fails.
        if (!CommitLayout(new(Locked: true))) forcedLocked = true;
        RefreshLayout();
    }
    internal bool BeginLayoutEdit(bool resize, System.Drawing.Point cursor, string? groupId = null)
    {
        if (!CanEdit || edit is not null || currentDefinition is not { } definition || currentFrame is null) return false;
        double dpi = GetDpiForWindow(new WindowInteropHelper(this).Handle) / 96.0;
        var panel = groupId is not null ? dockPanels.GetValueOrDefault(groupId).Panel : null;
        if (groupId is not null && panel is null) return false;
        var bounds = panel is null ? W.Rect.Empty : panel.TransformToAncestor(this).TransformBounds(new(0, 0, panel.ActualWidth, panel.ActualHeight));
        edit = new(definition, definition, currentFrame, cursor, wholeBounds, resize, wholeBounds.Width / naturalSize.Width / dpi,
            groupId, panel is null ? null : new(bounds.Width * dpi, bounds.Height * dpi));
        return true;
    }
    internal void UpdateLayoutEdit(System.Drawing.Point cursor)
    {
        if (edit is not { } gesture || currentState is null) return;
        int dx = cursor.X - gesture.Cursor.X, dy = cursor.Y - gesture.Cursor.Y;
        if (gesture.GroupId is { } groupId && gesture.GroupSize is { } groupSize)
        {
            double initial = gesture.Original.DockGroups!.Single(g => g.Id == groupId).Scale;
            double next = Math.Clamp(initial * (1 + (dx * groupSize.Width + dy * groupSize.Height) / (groupSize.Width * groupSize.Width + groupSize.Height * groupSize.Height)), .5, 2);
            if (next == gesture.Preview.DockGroups!.Single(g => g.Id == groupId).Scale) return;
            edit = gesture with { Preview = new SpellOverlayLayoutChange(GroupId: groupId, GroupScale: next).Apply(gesture.Original) };
            Render(edit.Preview, currentState); Place(edit.Preview, gesture.Frame); lastLayoutMovement = LayoutClock(); return;
        }
        double scale = gesture.Original.Scale;
        if (gesture.Resize)
        {
            // Project the drag onto the diagonal to scale text, icons and both dimensions together.
            double w = gesture.Bounds.Width, h = gesture.Bounds.Height;
            scale = Math.Clamp(gesture.VisibleScale * (1 + (dx * w + dy * h) / (w * w + h * h)), .5, 2);
        }
        double dpi = GetDpiForWindow(new WindowInteropHelper(this).Handle) / 96.0;
        var point = new System.Drawing.Point(gesture.Bounds.X + (gesture.Resize ? 0 : dx), gesture.Bounds.Y + (gesture.Resize ? 0 : dy));
        var areas = ReadDesktopAreas();
        var placementFrame = gesture.Original.FollowCharacterWindow ? gesture.Frame : SpellOverlayLayout.DesktopFrame(point, areas,
            SpellOverlayLayout.NaturalPixels(naturalSize.Width, naturalSize.Height, dpi, scale));
        var size = SpellOverlayLayout.Size(naturalSize.Width, naturalSize.Height, dpi, scale, placementFrame);
        var position = SpellOverlayLayout.Position(point, size, gesture.Frame);
        var freePoint = WindowLayouts.Clamp(point, size, [placementFrame.Area]);
        bool samePosition = gesture.Original.FollowCharacterWindow ? position.X == gesture.Preview.X && position.Y == gesture.Preview.Y
            : gesture.Preview.FreePosition == new OverlayDesktopPosition(freePoint.X, freePoint.Y);
        if (samePosition && scale == gesture.Preview.Scale) return;
        var previousBounds = wholeBounds;
        var change = gesture.Original.FollowCharacterWindow ? new SpellOverlayLayoutChange(Scale: scale, Frame: gesture.Frame, Position: position)
            : new SpellOverlayLayoutChange(Scale: scale, FreePosition: new(freePoint.X, freePoint.Y));
        edit = gesture with { Preview = change.Apply(gesture.Original) };
        Render(edit.Preview, currentState); Place(edit.Preview, gesture.Frame);
        if (wholeBounds != previousBounds) lastLayoutMovement = LayoutClock();
    }
    internal void EndLayoutEdit(bool cancelled)
    {
        if (edit is not { } gesture) return;
        edit = null; currentDefinition = gesture.Original;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (!cancelled && gesture.Preview != gesture.Original)
            CommitLayout(gesture.GroupId is { } groupId ? new(GroupId: groupId, GroupScale: gesture.Preview.DockGroups!.Single(g => g.Id == groupId).Scale)
                : gesture.Original.FollowCharacterWindow ? new(Scale: gesture.Preview.Scale, Frame: gesture.Frame, Position: new(gesture.Preview.X, gesture.Preview.Y))
                : new(Scale: gesture.Preview.Scale, FreePosition: gesture.Preview.FreePosition));
        RefreshLayout(); CheckAutoRelock();
    }
    private void CancelLayoutEdit()
    {
        if (edit is { } gesture) { edit = null; currentDefinition = gesture.Original; }
        if (IsMouseCaptured) ReleaseMouseCapture();
    }
    internal void ToggleLayoutLock(bool? locked = null)
    {
        if (currentDefinition is null) return;
        EndButtonEditing();
        CancelLayoutEdit(); CommitLayout(new(Locked: locked ?? !currentDefinition.Locked)); RefreshLayout();
    }
    private bool CommitLayout(SpellOverlayLayoutChange change)
    {
        if (saveLayout is null || currentDefinition is null) return false;
        try { currentDefinition = saveLayout(currentDefinition, change); layoutError = null; forcedLocked = false; return true; }
        catch (Exception error) { layoutError = error.Message; return false; }
    }
    private void RefreshLayout()
    {
        if (currentDefinition is not { } definition || currentState is null) return;
        Render(definition, currentState); Place(definition, currentFrame);
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetCursorPos(out System.Drawing.Point point);
}
