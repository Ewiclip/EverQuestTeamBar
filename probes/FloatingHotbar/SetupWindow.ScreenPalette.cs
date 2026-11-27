using System.Windows.Automation;
using C = System.Windows.Controls;
using I = System.Windows.Input;
using W = System.Windows;

namespace TeamBar;

public partial class SetupWindow
{
    private sealed record InlineChip(GameViewMapToken Token, string Label, string Hint, bool Enabled = true);
    private readonly HashSet<string> inlineNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<GameViewWindow, int> inlineWindowNumbers = [];
    private GameSession[] inlineWindows = [];
    private InlineChip[] inlineChips = [];
    private GameViewMapToken? inlineToken;
    private WindowIdentification? inlineIdentification;

    private void ResetInlinePalette()
    {
        inlineIdentification?.Dispose();
        inlineIdentification = null;
        inlineToken = null;
        inlineNames.Clear();
        inlineWindowNumbers.Clear();
        inlineWindows = [];
        inlineChips = [];
        InlinePalette.Children.Clear();
        InlineCharacterName.Clear();
    }

    private void RefreshInlineRoster(GameSession[] windows)
    {
        // WPF pumps timers during a drag. Keep the source and its target alive
        // until the pointer is released; the next roster tick can refresh them.
        if (I.Mouse.LeftButton == I.MouseButtonState.Pressed) return;
        var oldKeys = inlineWindows.Select(GameViewWindow.From).ToHashSet();
        inlineWindows = windows.OrderBy(w => w.StartedUtcTicks).ThenBy(w => w.ProcessId).ToArray();
        foreach (var session in inlineWindows)
            inlineWindowNumbers.TryAdd(GameViewWindow.From(session), inlineWindowNumbers.Count + 1);
        var keys = inlineWindows.Select(GameViewWindow.From).ToHashSet();
        bool labelsChanged = inlineDraft is { } draft && draft.Screens.Any(screen =>
            screen.Windows.Where((w, i) => w is not null && screen.Characters[i].Length == 0)
                .OfType<GameViewWindow>().Any(w => oldKeys.Contains(w) != keys.Contains(w)));
        if (labelsChanged) DrawInlineEditor();
        else UpdateInlinePalette();
    }

    private string InlineWindowLabel(GameViewWindow window) =>
        inlineWindowNumbers.TryGetValue(window, out int number) && inlineWindows.Any(w => GameViewWindow.From(w) == window)
            ? $"Window {number}" : $"Closed window · PID {window.ProcessId}";

    internal string? InlineWindowCharacter(GameViewWindow window)
    {
        var names = clients.Where(c => window.Matches(c.Client)).Select(c => c.Client.Character.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        return names.Length == 1 ? names[0] : null;
    }

    private void AssignInlineCharacter(GameViewInlineDraft.Screen screen, int slot, GameViewMapToken token)
    {
        if (inlineDraft is not { } draft) return;
        var live = new Dictionary<GameViewWindow, string>();
        foreach (var session in inlineWindows)
        {
            var key = GameViewWindow.From(session);
            if (InlineWindowCharacter(key) is { } name) live[key] = name;
        }
        string? character = token.Character;
        if (character is null && token.Window is { } window) live.TryGetValue(window, out character);
        draft.Assign(screen, slot, character, token.Window, live);
    }

    private void UpdateInlinePalette()
    {
        if (inlineDraft is not { } draft) return;
        var names = team.Characters.Select(c => c.Name).Concat(clients.Select(c => c.Client.Character.Name))
            .Concat(ScreenLayoutPicker.Items.OfType<GameViewLayout>().SelectMany(l => l.Characters))
            .Concat(draft.Screens.SelectMany(s => s.Characters));
        // A character displaced from their only slot stays available to place elsewhere.
        inlineNames.UnionWith(names.Where(n => !string.IsNullOrWhiteSpace(n)));
        var entries = new List<InlineChip>
        {
            new(GameViewMapToken.Star, "Swap destination", draft.TotalSlots >= 2
                ? "Place the star on the slot to swap into." : "Configure at least two slots to place the swap star.", draft.TotalSlots >= 2)
        };
        entries.AddRange(inlineNames.Order(StringComparer.OrdinalIgnoreCase).Select(n => new InlineChip(new(n), n, $"Assign {n} to a slot.")));
        foreach (var session in inlineWindows)
        {
            var key = GameViewWindow.From(session);
            string? name = InlineWindowCharacter(key);
            string label = InlineWindowLabel(key) + (string.IsNullOrEmpty(name) ? "" : $" · {name}");
            entries.Add(new(new(name, key), label, $"Assign this EQ window · PID {key.ProcessId}"));
        }
        InlineIdentifyWindows.IsEnabled = inlineWindows.Length > 0;
        if (entries.SequenceEqual(inlineChips)) return;
        inlineChips = entries.ToArray();
        InlinePalette.Children.Clear();
        foreach (var entry in entries) InlinePalette.Children.Add(MakeInlineChip(entry));
        SelectInlineToken(entries.Any(e => e.Enabled && e.Token == inlineToken) ? inlineToken : null);
    }

    private C.Primitives.ToggleButton MakeInlineChip(InlineChip entry)
    {
        var chip = new C.Primitives.ToggleButton { Tag = entry.Token, Content = entry.Label, IsEnabled = entry.Enabled, ToolTip = entry.Hint };
        chip.SetResourceReference(StyleProperty, "LayoutAssignmentChip");
        AutomationProperties.SetName(chip, entry.Label);
        AutomationProperties.SetHelpText(chip, entry.Hint);
        if (entry.Token == GameViewMapToken.Star)
        {
            var badge = new C.Border { Child = GameViewScreenMap.SwapStar(14), Padding = new W.Thickness(2), CornerRadius = new W.CornerRadius(3) };
            badge.SetResourceReference(C.Border.BackgroundProperty, "SwapTargetSurface");
            chip.Content = badge;
            chip.Width = chip.Height = 30;
            chip.Padding = new W.Thickness(5);
            C.ToolTipService.SetShowOnDisabled(chip, true);
        }
        chip.Click += (_, _) => SelectInlineToken(inlineToken == entry.Token ? null : entry.Token);
        W.Point? start = null;
        chip.PreviewMouseLeftButtonDown += (_, e) => start = e.GetPosition(chip);
        chip.PreviewMouseLeftButtonUp += (_, _) => start = null;
        chip.PreviewMouseMove += (_, e) =>
        {
            if (start is not { } origin || e.LeftButton != I.MouseButtonState.Pressed || !chip.IsMouseCaptured) return;
            var delta = e.GetPosition(chip) - origin;
            if (Math.Abs(delta.X) < W.SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(delta.Y) < W.SystemParameters.MinimumVerticalDragDistance) return;
            start = null;
            chip.ReleaseMouseCapture();
            e.Handled = true;
            GameViewScreenMap.BeginDrag(chip, entry.Token);
        };
        return chip;
    }

    private void SelectInlineToken(GameViewMapToken? token)
    {
        inlineToken = token;
        foreach (var chip in InlinePalette.Children.OfType<C.Primitives.ToggleButton>()) chip.IsChecked = Equals(chip.Tag, token);
    }

    private void InlineAddName_Click(object sender, W.RoutedEventArgs e) => AddInlineName();

    private void InlineCharacterName_KeyDown(object sender, I.KeyEventArgs e)
    {
        if (e.Key != I.Key.Enter) return;
        e.Handled = true;
        AddInlineName();
    }

    private void AddInlineName()
    {
        string name = InlineCharacterName.Text.Trim();
        if (name.Length == 0 || name.Any(char.IsControl))
        {
            ScreenFeedback("Enter a character name.", error: true);
            InlineCharacterName.Focus();
            return;
        }
        inlineNames.Add(name);
        UpdateInlinePalette();
        inlineNames.TryGetValue(name, out var spelling);
        SelectInlineToken(new(spelling));
        InlineCharacterName.Clear();
        ScreenFeedback("");
    }

    private void InlineIdentify_Click(object sender, W.RoutedEventArgs e)
    {
        try
        {
            RefreshInlineRoster(layoutWindows());
            inlineIdentification?.Dispose();
            var targets = inlineWindows.Select(w => new WindowIdentificationTarget(inlineWindowNumbers[GameViewWindow.From(w)], w)).ToArray();
            inlineIdentification = new(targets, message => ScreenFeedback(message), frameReader: WindowIdentification.ReadScreenFrame, viewSurface: session =>
                screensHost?.ActiveGameViewSession is { } view && view.TryGetVisibleTile((nint)session.Window, out var owner, out var bounds)
                    ? (owner.ToInt64(), bounds) : null);
        }
        catch (Exception error) { ScreenFeedback(error.Message, error: true); }
    }
}
