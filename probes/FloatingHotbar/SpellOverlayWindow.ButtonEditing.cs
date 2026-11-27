using D = System.Drawing;
using W = System.Windows;
using C = System.Windows.Controls;
using I = System.Windows.Input;
using M = System.Windows.Media;
using System.Windows.Interop;

namespace TeamBar;

internal sealed partial class SpellOverlayWindow
{
    private C.ControlTemplate? editControlTemplate;
    private C.ControlTemplate EditControlTemplate() => editControlTemplate ??= (C.ControlTemplate)W.Markup.XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
          <Border x:Name="Face" CornerRadius="5" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                  BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}">
            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Face" Property="Opacity" Value=".78"/></Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Face" Property="BorderBrush" Value="{Binding Foreground, RelativeSource={RelativeSource TemplatedParent}}"/><Setter TargetName="Face" Property="BorderThickness" Value="2"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """);
    private bool editingButtons;
    private Action<SpellOverlayWindow, W.FrameworkElement, SpellDockSide, string?, string?, int, bool>? openButtonEditor;
    private string? buttonEditError;
    private W.Window? buttonEditor;
    private W.FrameworkElement? editorAnchor;
    private SpellDockSide editorSide;
    private D.Rectangle lastAnchorInDisplay;
    private D.Point? editShellOrigin;
    private GameFrame? editAnchorFrame;

    internal string? ButtonEditorOverlayId => currentDefinition?.Id;
    internal Action<SpellOverlayWindow, W.FrameworkElement, SpellDockSide, string?, string?, int, bool>? OpenButtonEditor
    {
        get => openButtonEditor;
        set
        {
            openButtonEditor = value;
            if (value is null) EndButtonEditing();
            UpdateButtonChrome();
        }
    }

    private void UpdateButtonChrome()
    {
        layoutLock?.SetButtonEditing(openButtonEditor is not null, editingButtons);
        heading.Margin = new(0, 0, layoutLock is null ? 0 : layoutLock.ButtonCount * 28 + 6, 5);
        measureNeeded = true;
    }

    internal void ToggleButtonEditing()
    {
        if (openButtonEditor is null || currentDefinition is null || clickBusy) return;
        CancelLayoutEdit(); CancelSpellClick();
        if (editingButtons) EndButtonEditing();
        else
        {
            if (IsVisible && W.PresentationSource.FromVisual(shell) is not null)
            {
                var origin = shell.PointToScreen(new(0, 0));
                editShellOrigin = new((int)Math.Round(origin.X), (int)Math.Round(origin.Y));
                editAnchorFrame = currentFrame;
            }
            editingButtons = true;
            buttonEditError = null;
            UpdateButtonChrome();
        }
        RefreshLayout();
    }

    private void EndButtonEditing()
    {
        if (!editingButtons) return;
        editingButtons = false; editShellOrigin = null; editAnchorFrame = null;
        buttonEditError = null; CancelSpellClick();
        buttonEditor?.Close();
        UpdateButtonChrome();
    }

    private void RequestButtonEditor(W.FrameworkElement anchor, SpellDockSide side, string? group, string? id, int column, bool addColumn = false)
    {
        if (!editingButtons || currentDefinition is null || openButtonEditor is null) return;
        try
        {
            buttonEditError = null; UpdateClickHint();
            openButtonEditor(this, anchor, side, group, id, column, addColumn);
        }
        catch (Exception error)
        {
            buttonEditError = error.Message; UpdateClickHint();
        }
    }

    internal void TrackButtonEditor(W.Window editor)
    {
        if (ReferenceEquals(buttonEditor, editor)) return;
        buttonEditor?.Close();
        buttonEditor = editor;
        editor.Closed += (_, _) =>
        {
            if (ReferenceEquals(buttonEditor, editor)) { buttonEditor = null; editorAnchor = null; }
        };
    }

    internal void ShowButtonEditor(W.Window editor, W.FrameworkElement anchor, SpellDockSide side)
    {
        if (!editingButtons || !IsVisible) return;
        TrackButtonEditor(editor);
        editorAnchor = anchor; editorSide = side;
        editor.Owner = this; editor.WindowStartupLocation = W.WindowStartupLocation.Manual;
        editor.Left = Left; editor.Top = Top;
        var cursor = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).WorkingArea;
        var dpi = M.VisualTreeHelper.GetDpi(this);
        editor.MaxWidth = Math.Min(editor.MaxWidth, cursor.Width / dpi.DpiScaleX);
        editor.MaxHeight = Math.Min(editor.MaxHeight, cursor.Height / dpi.DpiScaleY);
        editor.SizeChanged += (_, _) => PositionButtonEditor();
        try
        {
            editor.Show(); PositionButtonEditor(); editor.Activate();
        }
        catch { editor.Close(); throw; }
    }

    private void PositionButtonEditor()
    {
        if (buttonEditor is not { IsVisible: true } editor || editorAnchor is null) return;
        var handle = new WindowInteropHelper(editor).Handle;
        if (handle == 0 || !GetWindowRect(handle, out var actual)) return;
        // A normal live poll can rebuild the clicked row while its editor is open.
        // Retain the last position relative to the overlay across those rebuilds.
        if (W.PresentationSource.FromVisual(editorAnchor) is not null)
        {
            var start = editorAnchor.PointToScreen(new(0, 0));
            var end = editorAnchor.PointToScreen(new(editorAnchor.ActualWidth, editorAnchor.ActualHeight));
            lastAnchorInDisplay = new((int)start.X - wholeBounds.X, (int)start.Y - wholeBounds.Y,
                Math.Max(1, (int)(end.X - start.X)), Math.Max(1, (int)(end.Y - start.Y)));
        }
        var anchor = lastAnchorInDisplay; anchor.Offset(wholeBounds.Location);
        var workArea = System.Windows.Forms.Screen.FromRectangle(anchor).WorkingArea;
        var size = new D.Size(actual.Right - actual.Left, actual.Bottom - actual.Top);
        if (size.Width <= 0 || size.Height <= 0) return;
        var point = EditorPosition(anchor, size, workArea, editorSide);
        if (point.X != actual.Left || point.Y != actual.Top)
            SetWindowPos(handle, 0, point.X, point.Y, 0, 0, 0x15); // Keep size, z-order and activation.
    }

    private static D.Point EditorPosition(D.Rectangle anchor, D.Size panel, D.Rectangle area, SpellDockSide side)
    {
        const int gap = 5;
        int right = anchor.Right + gap, left = anchor.Left - panel.Width - gap;
        int below = anchor.Bottom + gap, above = anchor.Top - panel.Height - gap;
        int x = anchor.Left, y = anchor.Top;
        switch (side)
        {
            case SpellDockSide.Left: x = left >= area.Left || right + panel.Width > area.Right ? left : right; break;
            case SpellDockSide.Right: x = right + panel.Width <= area.Right || left < area.Left ? right : left; break;
            case SpellDockSide.Top: y = above >= area.Top || below + panel.Height > area.Bottom ? above : below; break;
            case SpellDockSide.Bottom: y = below + panel.Height <= area.Bottom || above < area.Top ? below : above; break;
        }
        return new(Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - panel.Width)),
            Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - panel.Height)));
    }

    private C.Button MakeAddButton(string label, string action, SpellDockSide side, string? group = null,
        int column = 0, bool addColumn = false)
    {
        bool edge = group is null;
        var button = new C.Button { Content = label, FontSize = edge ? 19 : 11, Padding = new(3, 0, 3, 0), Margin = new(1),
            MinWidth = 26, MinHeight = edge ? 26 : 24, Foreground = Brush(theme.Accent), Background = Brush(theme.Bar),
            BorderBrush = Brush(theme.ButtonBorder), BorderThickness = new(1), Template = EditControlTemplate(),
            ToolTip = action, Cursor = I.Cursors.Hand };
        W.Automation.AutomationProperties.SetName(button, action);
        button.Click += (_, _) => RequestButtonEditor(button, side, group, null, column, addColumn);
        return button;
    }
}
