using System.Runtime.InteropServices;
using W = System.Windows;
using C = System.Windows.Controls;
using M = System.Windows.Media;
using System.Windows.Interop;
namespace TeamBar;

// Locked display: mouse-transparent, with a small interactive corner lock. Unlocked: drag anywhere.
internal sealed partial class SpellOverlayWindow : W.Window
{
    private readonly SpellIcons icons = new();
    private readonly C.StackPanel body = new();
    private readonly C.TextBlock heading = new(), target = new(), hint = new();
    private readonly C.Border shell;
    private FloatingTheme theme;
    private readonly Dictionary<(int, int, bool), (C.TextBlock Time, C.Image Icon, C.TextBlock Present, C.TextBlock Detail, C.Border Border)> rows = [];
    private readonly Dictionary<int, int> seenIcons = [];
    private string layoutKey = "";
    private W.Size naturalSize;
    private bool followsFrame, measureNeeded = true;
    internal SpellOverlayWindow(FloatingTheme theme,
        Func<SpellOverlayDefinition, SpellOverlayLayoutChange, SpellOverlayDefinition>? saveLayout = null, Action<string>? openSettings = null)
    {
        this.theme = theme;
        this.saveLayout = saveLayout;
        if (saveLayout is not null) layoutLock = new(this, theme, () =>
        {
            CancelSpellClick(); if (editingButtons) ToggleButtonEditing();
            if (currentDefinition is { } d) openSettings?.Invoke(d.Id);
        }, openSettings is not null);
        IsVisibleChanged += (_, _) => { if (!IsVisible) { EndButtonEditing(); CancelLayoutEdit(); CancelSpellClick(); layoutLock?.Hide(); } };
        Closed += (_, _) => { autoLockTimer.Stop(); EndButtonEditing(); CancelLayoutEdit(); clickShield?.Dispose(); clickShield = null; layoutLock?.CloseWithDisplay(); };
        Title = "Team Bar · Spell overlay"; ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        WindowStyle = W.WindowStyle.None; ResizeMode = W.ResizeMode.NoResize; AllowsTransparency = true;
        Background = M.Brushes.Transparent; FontFamily = new("Segoe UI"); IsHitTestVisible = false;
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        DpiChanged += (_, _) => measureNeeded = true;
        var stack = new C.StackPanel();
        heading.FontSize = 11; heading.Foreground = Brush(theme.Accent); heading.Margin = new(0, 0, saveLayout is null ? 0 : openSettings is null ? 32 : 62, 5);
        heading.TextTrimming = W.TextTrimming.CharacterEllipsis;
        target.FontSize = 17; target.FontWeight = W.FontWeights.SemiBold; target.Foreground = Brush(theme.Text);
        target.TextTrimming = W.TextTrimming.CharacterEllipsis; target.Margin = new(0, 0, 0, 10);
        hint.FontSize = 11; hint.Foreground = Brush(theme.DetailText ?? theme.Text); hint.Opacity = .8;
        hint.TextWrapping = W.TextWrapping.Wrap; hint.Margin = new(0, 9, saveLayout is null ? 0 : 24, 0);
        stack.Children.Add(heading); stack.Children.Add(target); stack.Children.Add(body); stack.Children.Add(hint);
        var content = new C.Grid(); content.Children.Add(stack);
        resizeGrip = new C.Border { Width = 22, Height = 22, HorizontalAlignment = W.HorizontalAlignment.Right,
            VerticalAlignment = W.VerticalAlignment.Bottom, Background = M.Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.SizeNWSE, Visibility = W.Visibility.Collapsed,
            ToolTip = "Drag to resize the overlay and its text",
            Child = new C.TextBlock { Text = "⤢", FontSize = 20, Foreground = Brush(theme.Accent), HorizontalAlignment = W.HorizontalAlignment.Center } };
        W.Automation.AutomationProperties.SetName(resizeGrip, "Resize overlay"); content.Children.Add(resizeGrip);
        shell = new C.Border { Background = Brush(theme.Bar), BorderBrush = Brush(theme.BarBorder), BorderThickness = new(1),
            CornerRadius = new(9), Padding = new(14), Child = content };
        InitializeDockLayout();
        Content = new C.Viewbox { Stretch = M.Stretch.Uniform, StretchDirection = C.StretchDirection.Both, Child = dockRoot };
        InitializeLayoutInput();
        SourceInitialized += (_, _) =>
        {
            var source = (HwndSource)W.PresentationSource.FromVisual(this);
            UpdateInputMode();
            source.AddHook((nint hwnd, int message, nint w, nint l, ref bool handled) =>
            {
                if (message == MouseShield.RoutedMouse)
                {
                    long packed = l.ToInt64(); RoutedSpellMouse((int)w, new(unchecked((short)packed), unchecked((short)(packed >> 16))));
                    handled = true; return 0;
                }
                if (!CanEdit && !editingButtons && message == 0x21) { handled = true; return 3; } // MA_NOACTIVATE
                if (!CanEdit && !editingButtons && !ClickSurface && message == 0x84) { handled = true; return -1; } // HTTRANSPARENT
                return 0;
            });
        };
    }
    private static M.SolidColorBrush Brush(string color) => new((M.Color)M.ColorConverter.ConvertFromString(color));
    internal void SetTheme(FloatingTheme value)
    {
        // Keep the current hit geometry intact until a click or layout gesture finishes.
        if (theme == value || edit is not null || pressedSpell is not null || clickBusy) return;
        theme = value;
        shell.Background = Brush(theme.Bar); shell.BorderBrush = Brush(theme.BarBorder);
        heading.Foreground = Brush(theme.Accent); target.Foreground = Brush(theme.Text);
        hint.Foreground = Brush(theme.DetailText ?? theme.Text);
        if (resizeGrip.Child is C.TextBlock glyph) glyph.Foreground = Brush(theme.Accent);
        layoutLock?.SetTheme(theme);
        layoutKey = ""; dockLayoutKey = ""; measureNeeded = true;
    }
    internal void Render(SpellOverlayDefinition definition, SpellOverlayState state)
    {
        if (edit is { } active && active.Original.FollowCharacterWindow != definition.FollowCharacterWindow) CancelLayoutEdit();
        definition = EffectiveDefinition(edit?.Preview ?? definition); currentDefinition = definition; currentState = state;
        RenderDockGroups(definition);
        ObserveLockState(); UpdateInputMode(); layoutLock?.SetDefinition(definition, layoutError);
        var allRows = state.Rows.SelectMany(row => new[] { (Row: row, Child: false) }
            .Concat((row.Children ?? []).Select(child => (Row: child, Child: true)))).ToArray();
        foreach (var (row, _) in allRows)
            if (row.Rule.Alternatives is not { Length: > 0 } && row.IconId is { } id) seenIcons[row.Rule.SpellId] = id;
        int? IconFor(WatchedSpellRow row) => row.IconId ?? (row.Rule.Alternatives is { Length: > 0 } ? row.Rule.IconId
            : seenIcons.TryGetValue(row.Rule.SpellId, out int id) ? id : null);
        void SetLabel(C.TextBlock label, string value)
        { if (label.Text != value) { label.Text = value; measureNeeded = true; } }
        SetLabel(heading, string.IsNullOrWhiteSpace(definition.Name) ? definition.CharacterPersona : definition.Name.ToUpperInvariant() + " · " + definition.CharacterPersona);
        SetLabel(target, state.Target);
        if (!HasClickMappings) { hint.TextWrapping = W.TextWrapping.Wrap; SetLabel(hint, state.Message); }
        else UpdateClickHint();
        if (Opacity != definition.Opacity) Opacity = definition.Opacity;
        if (shell.Width != definition.Width) { shell.Width = definition.Width; measureNeeded = true; }
        string key = string.Join("|", allRows.Select(r => $"{r.Row.Priority}:{r.Row.DisplayPriority}:{r.Row.Rule.SpellId}:{r.Row.State}:{r.Row.Name}:{r.Child}:{r.Row.Missing}:{r.Row.Rule.ClickKey}"));
        if (key != layoutKey || rows.Count != allRows.Length)
        {
            CancelSpellClick(); layoutKey = key; body.Children.Clear(); rows.Clear(); clickRows.Clear(); measureNeeded = true;
            foreach (var (row, child) in allRows)
            {
                var grid = new C.Grid { Margin = new(0, 3, 0, 3) };
                grid.ColumnDefinitions.Add(new() { Width = new(34) }); grid.ColumnDefinitions.Add(new() { Width = new(1, W.GridUnitType.Star) });
                // Keep the name lane stable across 10:00 -> 9:59 and hour-format changes.
                grid.ColumnDefinitions.Add(new() { Width = new(100) });
                var image = new C.Image { Width = 28, Height = 28, Source = icons.Get(IconFor(row)), VerticalAlignment = W.VerticalAlignment.Center };
                var iconFrame = new C.Grid(); iconFrame.Children.Add(new C.TextBlock { Text = "✦", FontSize = 22, Foreground = Brush(theme.Accent), HorizontalAlignment = W.HorizontalAlignment.Center });
                iconFrame.Children.Add(image); grid.Children.Add(iconFrame);
                var parent = definition.Spells[row.Priority - 1];
                var name = new C.TextBlock { Text = child ? row.Name : $"{row.DisplayPriority ?? row.Priority}. {row.Name}", FontSize = child ? 12 : 13, FontWeight = W.FontWeights.SemiBold,
                    Foreground = Brush(row.Missing ? "#FFB7BA" : theme.Text), TextTrimming = W.TextTrimming.CharacterEllipsis,
                    ToolTip = row.Name, VerticalAlignment = W.VerticalAlignment.Center, Margin = new(5, 0, 10, 0) };
                var labels = new C.StackPanel { VerticalAlignment = W.VerticalAlignment.Center }; labels.Children.Add(name);
                C.TextBlock DetailLabel() => new() { FontSize = 10, Foreground = Brush(row.Missing ? "#FFB7BA" : theme.Accent),
                    Margin = new(5, 2, 10, 0), TextTrimming = W.TextTrimming.CharacterEllipsis, Visibility = W.Visibility.Collapsed };
                var present = DetailLabel(); labels.Children.Add(present);
                var detail = DetailLabel(); labels.Children.Add(detail);
                C.Grid.SetColumn(labels, 1); grid.Children.Add(labels);
                var time = new C.TextBlock { FontFamily = new("Consolas"), FontSize = 13, VerticalAlignment = W.VerticalAlignment.Center,
                    TextAlignment = W.TextAlignment.Right, TextWrapping = W.TextWrapping.Wrap,
                    Foreground = Brush(row.Missing ? "#FFB7BA" : theme.Accent) };
                C.Grid.SetColumn(time, 2); grid.Children.Add(time);
                var rowBorder = new C.Border { Background = row.Missing ? Brush("#531F29") : Brush(theme.Button),
                    BorderBrush = row.Missing ? Brush("#BB5862") : Brush(theme.ButtonBorder), BorderThickness = new(1),
                    CornerRadius = new(5), Padding = new(7, 4, 7, 4), Margin = new(child ? 18 : 0, 0, 0, row.Children is null ? 5 : 3), Child = grid,
                    ToolTip = parent.ClickKey is { } mapped ? $"Press {mapped} on {definition.Character} · {parent.Name}" : "Display only" };
                W.Automation.AutomationProperties.SetName(rowBorder, rowBorder.ToolTip.ToString());
                var normalBorder = rowBorder.BorderBrush;
                rowBorder.MouseEnter += (_, _) => { if (ClickSurface && parent.ClickKey is not null) rowBorder.BorderBrush = Brush(theme.Accent); };
                rowBorder.MouseLeave += (_, _) => rowBorder.BorderBrush = normalBorder;
                body.Children.Add(rowBorder); clickRows.Add((rowBorder, parent));
                rows[(row.Priority, row.Rule.SpellId, child)] = (time, image, present, detail, rowBorder);
            }
        }
        UpdateClickHint();
        foreach (var (row, child) in allRows)
        {
            var controls = rows[(row.Priority, row.Rule.SpellId, child)];
            if (controls.Time.Text != row.Time) controls.Time.Text = row.Time;
            void SetDetail(C.TextBlock label, string text)
            {
                SetLabel(label, text);
                var visibility = text.Length == 0 ? W.Visibility.Collapsed : W.Visibility.Visible;
                if (label.Visibility != visibility) { label.Visibility = visibility; measureNeeded = true; }
                label.ToolTip = text;
            }
            string present = row.State == "Active" && row.Rule.Alternatives is { Length: > 0 }
                && !string.IsNullOrWhiteSpace(row.MatchedSpellName) ? "Present: " + row.MatchedSpellName : "";
            string caster = row.State == "Active" && row.Rule.Caster == SpellCasterKind.Anyone
                ? string.IsNullOrWhiteSpace(row.CasterName) ? "Caster unknown" : "Cast by " + row.CasterName : "";
            string detail = string.Join(" · ", new[] { child ? null : row.Rule.ClickKey, caster }.Where(s => !string.IsNullOrEmpty(s)));
            SetDetail(controls.Present, present); SetDetail(controls.Detail, detail);
            var parent = definition.Spells[row.Priority - 1];
            string action = parent.ClickKey is { } mapped ? $"Press {mapped} on {definition.Character} · {parent.Name}" : "Display only";
            string description = string.Join("\n", new[] { action, present, caster }.Where(s => s.Length > 0));
            controls.Border.ToolTip = description;
            W.Automation.AutomationProperties.SetName(controls.Border, description);
            var icon = icons.Get(IconFor(row));
            if (!ReferenceEquals(controls.Icon.Source, icon)) controls.Icon.Source = icon;
        }
        if (measureNeeded)
        {
            // Child invalidations normally bubble during the next layout pass. Resolve them now so
            // placement uses the new content height on this poll, not the previous row count.
            foreach (W.UIElement child in ((C.Grid)shell.Child).Children) child.InvalidateMeasure();
            shell.Child.InvalidateMeasure(); shell.InvalidateMeasure();
            shell.Measure(new(definition.Width, double.PositiveInfinity));
            dockRoot.InvalidateMeasure(); dockRoot.Measure(new(double.PositiveInfinity, double.PositiveInfinity));
            naturalSize = dockRoot.DesiredSize; measureNeeded = false;
            // Previews use natural dimensions. Live placement owns the HWND size once attached to a frame.
            // Reassigning these every poll expands a fitted window before Place shrinks it again.
            if (!followsFrame) { Width = naturalSize.Width * definition.Scale; Height = naturalSize.Height * definition.Scale; }
        }
    }
    internal void Place(SpellOverlayDefinition definition, GameFrame? frame)
    {
        if (frame is null) { CancelLayoutEdit(); currentFrame = null; if (IsVisible) Hide(); return; }
        if (edit is { } gesture && definition.FollowCharacterWindow && frame != gesture.Frame) CancelLayoutEdit();
        currentFrame = frame; definition = EffectiveDefinition(edit?.Preview ?? definition); currentDefinition = definition;
        followsFrame = true;
        if (!IsVisible) Show();
        nint handle = new WindowInteropHelper(this).Handle;
        double dpi = GetDpiForWindow(handle) / 96.0;
        var areas = ReadDesktopAreas();
        var free = definition.FreePosition ?? freeSeed;
        if (!definition.FollowCharacterWindow && free is null)
        {
            var initialSize = SpellOverlayLayout.Size(naturalSize.Width, naturalSize.Height, dpi, definition.Scale, frame);
            var initial = AnchorPositions.Project(new(definition.X, definition.Y), initialSize, frame.Area);
            free = freeSeed = new(initial.X, initial.Y);
        }
        var placementFrame = definition.FollowCharacterWindow ? frame : SpellOverlayLayout.DesktopFrame(new(free!.X, free.Y), areas,
            SpellOverlayLayout.NaturalPixels(naturalSize.Width, naturalSize.Height, dpi, definition.Scale));
        var size = SpellOverlayLayout.Size(naturalSize.Width, naturalSize.Height, dpi, definition.Scale, placementFrame);
        var positions = new CharacterPositions(definition.Character, new(definition.X, definition.Y), definition.Screens ?? []);
        var point = definition.FollowCharacterWindow ? AnchorPositions.Resolve(positions, size, frame)
            : WindowLayouts.Clamp(new(free!.X, free.Y), size, [placementFrame.Area]);
        // While arranging buttons, keep the center bar under the same screen point
        // as side controls appear. Clamp the complete display to its placement area.
        if (editingButtons && editShellOrigin is not null) point = wholeBounds.Location;
        if (wholeBounds != new System.Drawing.Rectangle(point, size)) CancelSpellClick();
        wholeBounds = new(point, size);
        SetBounds(handle, wholeBounds);
        UpdateLayout();
        if (editingButtons && editShellOrigin is { } origin)
        {
            if (definition.FollowCharacterWindow && editAnchorFrame is { } initial)
                origin.Offset(frame.Area.Left - initial.Area.Left, frame.Area.Top - initial.Area.Top);
            var actual = shell.PointToScreen(new(0, 0));
            point.Offset(origin.X - (int)Math.Round(actual.X), origin.Y - (int)Math.Round(actual.Y));
            wholeBounds = new(WindowLayouts.Clamp(point, size, [placementFrame.Area]), size);
            SetBounds(handle, wholeBounds);
            PositionButtonEditor();
        }
        if (layoutLock is not null && size.Width >= 40 && size.Height >= 32)
        {
            // Ownership keeps the corner above the body when an unlocked body takes focus.
            if (layoutLock.Owner is null) layoutLock.Owner = this;
            layoutLock.SetDefinition(definition, layoutError);
            if (!layoutLock.IsVisible) layoutLock.Show();
            var shellBounds = shell.TransformToAncestor(this).TransformBounds(new(0, 0, shell.ActualWidth, shell.ActualHeight));
            double factor = size.Width / naturalSize.Width;
            int side = Math.Max(1, (int)Math.Round(28 * factor)), inset = Math.Max(1, (int)Math.Round(4 * factor));
            var corner = PointToScreen(new(shellBounds.Right, shellBounds.Top));
            SetBounds(new WindowInteropHelper(layoutLock).Handle, new((int)corner.X - side * layoutLock.ButtonCount - inset, (int)corner.Y + inset, side * layoutLock.ButtonCount, side));
        }
        else layoutLock?.Hide();
    }
    private static void SetBounds(nint handle, System.Drawing.Rectangle bounds)
    {
        if (!GetWindowRect(handle, out var current) || current.Left != bounds.X || current.Top != bounds.Y
            || current.Right - current.Left != bounds.Width || current.Bottom - current.Top != bounds.Height)
            SetWindowPos(handle, new(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x10);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Bounds { internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Bounds bounds);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtrW(nint window, int index, nint value);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
