using W = System.Windows;
using C = System.Windows.Controls;
using I = System.Windows.Input;
using M = System.Windows.Media;

namespace TeamBar;

internal static class OverlayButtonIcons
{
    internal sealed record Choice(string? Id, string Label, string Symbol);
    internal static readonly Choice[] Choices =
    [
        new(null, "No icon", "—"), new("swords", "Attack", "⚔"), new("shield", "Defend", "⛨"),
        new("follow", "Follow", "➜"), new("melody", "Melody", "♫"), new("heal", "Heal", "✚"),
        new("star", "Spell", "✦"), new("target", "Target", "◎"), new("flag", "Rally", "⚑"),
        new("heart", "Health", "♥"), new("sun", "Buff", "☀"), new("skull", "Debuff", "☠")
    ];
    internal static string Glyph(string? id) => id is null ? "" : Choices.FirstOrDefault(c => c.Id == id)?.Symbol ?? "";

    // Editors need a way to identify a saved action even when its face has no text.
    internal static string ManagementName(string? label, string? icon, string? shortcut = null)
    {
        if (!string.IsNullOrWhiteSpace(label)) return label;
        string? description = icon switch
        {
            null => null,
            _ when icon.StartsWith("eq:item:", StringComparison.Ordinal) => "Item icon #" + icon[8..],
            _ when icon.StartsWith("eq:spell:", StringComparison.Ordinal) => "Spell icon #" + icon[9..],
            _ => Choices.FirstOrDefault(c => c.Id == icon)?.Label
        };
        if (string.IsNullOrWhiteSpace(shortcut)) return description ?? "Unnamed button";
        return description is null ? shortcut : description + " · " + shortcut;
    }
}

internal partial class OverlayButtonEditor : W.Window
{
    private OverlayButtonEditor(FloatingTheme theme, string title, double width)
    {
        InitializeComponent();
        void Color(string key, string hex) => Resources[key] = new M.SolidColorBrush((M.Color)M.ColorConverter.ConvertFromString(hex));
        Color("Surface", theme.Bar); Color("Edge", theme.BarBorder); Color("Text", theme.Text);
        Color("Muted", theme.DetailText ?? theme.Text); Color("Field", theme.Button); Color("Line", theme.ButtonBorder);
        Color("Accent", theme.Accent); Color("HoverSurface", theme.Bar); Color("SelectedOption", theme.Button);
        Color("PopupBorder", theme.BarBorder); Color("PopupSurface", theme.Bar); Color("HoverBorder", theme.Accent);
        Color("Scrollbar", theme.ButtonBorder); Color("ScrollbarHover", theme.Accent);
        var feedback = FloatingPainter.ConflictColor(theme);
        Resources["Feedback"] = new M.SolidColorBrush(M.Color.FromRgb(feedback.R, feedback.G, feedback.B));
        Title = title;
        Heading.Text = title;
        Width = width;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == I.Key.Escape && !e.Handled && !Recording) { e.Handled = true; DismissPanel(); }
        };
        PreviewMouseDown += (_, _) => { if (recording?.Record.IsMouseOver != true) StopRecording(); };
        Deactivated += (_, _) => { StopRecording(); consumedKeys.Clear(); };
        SourceInitialized += AttachRecorder;
        Closed += (_, _) => CancelIconWork();
    }

    internal static W.Window CreatePicker(FloatingTheme theme, SavedButton[] saved, IEnumerable<string> placedIds,
        Action<SavedButton> add, Action create, string title = "Add a button")
    {
        var window = new OverlayButtonEditor(theme, title, 318);
        var placed = placedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var choices = saved.OrderBy(s => s.Value.ManagementName, StringComparer.CurrentCultureIgnoreCase)
            .Select(s => (Saved: s, Info: PickerButtonInfo.From(s.Value, placed.Contains(s.Value.Id)))).ToArray();
        var searchGrid = new C.Grid { Margin = new(0, 0, 0, 6) };
        var search = new C.TextBox(); NameControl(search, "Search saved buttons");
        var placeholder = new C.TextBlock { Text = "Search saved buttons…", Margin = new(10, 0, 0, 0), Opacity = .65,
            VerticalAlignment = W.VerticalAlignment.Center, IsHitTestVisible = false };
        searchGrid.Children.Add(search); searchGrid.Children.Add(placeholder); window.Body.Children.Add(searchGrid);
        var list = new C.StackPanel();
        window.Body.Children.Add(Scroll(list, 250));
        void Filter()
        {
            list.Children.Clear();
            placeholder.Visibility = search.Text.Length == 0 ? W.Visibility.Visible : W.Visibility.Hidden;
            foreach (var choice in choices.Where(c => c.Info.SearchText.Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)))
            {
                var row = window.Button(choice.Info.Name, () => window.Commit(() => add(choice.Saved)));
                var content = new C.StackPanel { Orientation = C.Orientation.Horizontal };
                content.Children.Add(window.SavedIcon(choice.Saved.Value.Icon, 23));
                content.Children.Add(new C.TextBlock { Text = choice.Info.Name, VerticalAlignment = W.VerticalAlignment.Center });
                row.Content = content; row.Padding = new(10, 6, 10, 6); row.Margin = new(0, 2, 0, 2);
                row.HorizontalContentAlignment = W.HorizontalAlignment.Left;
                row.IsEnabled = !choice.Info.Included;
                row.ToolTip = !row.IsEnabled ? "Already in this group" : string.Join("\n", choice.Info.Mappings);
                list.Children.Add(row);
            }
            if (list.Children.Count == 0) list.Children.Add(new C.TextBlock { Text = choices.Length == 0 ? "No saved buttons yet." : "No matching buttons.", Margin = new(4, 6, 4, 8) });
        }
        search.TextChanged += (_, _) => Filter();
        Filter();
        var createButton = window.Button("+  Create button…", () => window.Commit(create));
        createButton.Margin = new(0, 9, 0, 0); createButton.SetResourceReference(C.Control.BorderBrushProperty, "Accent");
        window.Body.Children.Add(createButton);
        window.ContentRendered += (_, _) => search.Focus();
        return window;
    }

    internal static W.Window CreateEditor(FloatingTheme theme, StandaloneButton draft, bool creating, int placements,
        Action<StandaloneButton> save, Action? remove = null)
    {
        var window = new OverlayButtonEditor(theme, creating ? "Create button" : "Edit shortcuts", 464);
        window.BuildEditor(draft, creating, placements, save, remove);
        return window;
    }

    internal static W.Window CreateMissing(FloatingTheme theme, Action remove)
    {
        var window = new OverlayButtonEditor(theme, "Missing saved button", 286);
        window.Body.Children.Add(window.Button("Remove from this bar", () => window.Commit(remove)));
        return window;
    }

    private void BuildEditor(StandaloneButton draft, bool creating, int placements, Action<StandaloneButton> save, Action? remove)
    {
        var team = draft.AsTeam();
        var options = team.Characters.Select(c => new TargetSelector(CharacterId: c.Id))
            .Concat(team.Personas.Where(p => p.Class is not null).Select(p => new TargetSelector(PersonaId: p.Id)))
            .Concat(Identity.Classes.Select(c => new TargetSelector(Class: c)))
            .Concat(draft.Rules.Select(r => r.Target))
            .DistinctBy(TeamEditing.SelectorKey)
            .Select(t => new RecipientChoice(TeamEditing.SelectorKey(t), TeamEditing.SelectorLabel(team, t), "", t)).ToArray();
        var info = new C.DockPanel();
        var name = new C.TextBox { Text = draft.Label, MaxLength = 24, VerticalContentAlignment = W.VerticalAlignment.Center };
        NameControl(name, "Button name");
        var nameLabel = new C.Label { Content = "Name (optional)", Target = name, Foreground = Foreground, Padding = new(0, 0, 8, 0), VerticalContentAlignment = W.VerticalAlignment.Center };
        info.Children.Add(nameLabel);
        string? icon = draft.Icon;
        var iconButton = Button("Choose button icon");
        iconButton.Padding = new(2); iconButton.Width = 38; iconButton.Height = 36;
        iconButton.Margin = new(0, 0, 5, 0); iconButton.ToolTip = "Choose button icon";
        void UpdateIcon() => iconButton.Content = SavedIcon(icon, 28, "◇");
        iconButton.Click += (_, _) => OpenIconBrowser(iconButton, icon, selected => { icon = selected; UpdateIcon(); });
        UpdateIcon(); info.Children.Add(iconButton); info.Children.Add(name); Body.Children.Add(info);
        if (placements > 1)
            Body.Children.Add(new C.TextBlock { Text = $"Shared across {placements} placements", Margin = new(0, 9, 0, 0) });
        Body.Children.Add(new C.TextBlock { Text = "Recipients", FontWeight = W.FontWeights.SemiBold, Margin = new(0, 11, 0, 3) });
        var list = new C.StackPanel();
        Body.Children.Add(Scroll(list, 240));
        var rules = new List<EditRow>();
        void Add(ButtonRule rule, bool choose = false)
        {
            var row = new EditRow(rule);
            rules.Add(row);
            var grid = new C.Grid { Margin = new(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = W.GridLength.Auto }); grid.ColumnDefinitions.Add(new() { Width = W.GridLength.Auto });
            var recipient = new C.ComboBox { ItemsSource = options, DisplayMemberPath = nameof(RecipientChoice.Label),
                SelectedItem = options.First(o => o.Target == rule.Target), MinHeight = 34, MaxDropDownHeight = 260,
                ToolTip = "Choose recipient", IsTextSearchEnabled = true };
            NameControl(recipient, "Recipient");
            recipient.SelectionChanged += (_, _) =>
            {
                if (recipient.SelectedItem is RecipientChoice selected) row.Target = selected.Target;
            };
            var record = Button("Record shortcut", () => BeginRecording(row));
            row.Record = record; SetKeycaps(row); record.MinWidth = 90; record.Margin = new(4, 0, 0, 0); record.Padding = new(4, 3, 4, 3);
            var delete = Button("Remove recipient", () => { StopRecording(); rules.Remove(row); list.Children.Remove(grid); });
            delete.Content = "×"; delete.Width = 27; delete.Margin = new(4, 0, 0, 0); delete.Padding = new(0); delete.ToolTip = "Remove recipient";
            C.Grid.SetColumn(record, 1); C.Grid.SetColumn(delete, 2);
            grid.Children.Add(recipient); grid.Children.Add(record); grid.Children.Add(delete); list.Children.Add(grid);
            if (choose) { recipient.BringIntoView(); recipient.Focus(); recipient.IsDropDownOpen = true; }
        }
        foreach (var rule in draft.Rules) Add(rule);
        var add = Button("+ Add recipient", () =>
        {
            if (rules.Count >= 24) { Error("A button supports up to 24 recipient rows."); return; }
            var choice = options.FirstOrDefault(c => rules.All(r => r.Target != c.Target)) ?? options[0];
            Add(new(choice.Target, ""), choose: true);
        });
        add.Margin = new(0, 5, 0, 0); add.HorizontalAlignment = W.HorizontalAlignment.Left; Body.Children.Add(add);
        var footer = new C.DockPanel { LastChildFill = false, Margin = new(0, 12, 0, 0) };
        if (remove is not null)
        {
            var drop = Button("Remove from this bar", () => Commit(remove));
            drop.Background = M.Brushes.Transparent; footer.Children.Add(drop);
        }
        var done = Button(creating ? "Add to bar" : "Done", () => Commit(() =>
        {
            if (rules.Count == 0) throw new InvalidDataException("Add at least one recipient.");
            if (rules.Any(r => r.Chord.Length == 0)) throw new InvalidDataException("Record a shortcut for each recipient.");
            var edited = draft with { Label = name.Text.Trim(), Icon = icon, Rules = rules.Select(r => new ButtonRule(r.Target, r.Chord)).ToArray() };
            StandaloneButtons.Validate(edited); save(edited);
        }));
        done.MinWidth = 85; done.SetResourceReference(C.Control.BackgroundProperty, "Accent");
        done.SetResourceReference(C.Control.ForegroundProperty, "Surface"); done.FontWeight = W.FontWeights.SemiBold;
        C.DockPanel.SetDock(done, C.Dock.Right); footer.Children.Add(done); Body.Children.Add(footer);
        if (creating) ContentRendered += (_, _) => name.Focus();
    }

    private C.Button Button(string label, Action? click = null)
    {
        var button = new C.Button { Content = label };
        NameControl(button, label);
        button.Click += (_, _) => { Error(""); click?.Invoke(); };
        return button;
    }
    private static void NameControl(W.UIElement element, string name) => W.Automation.AutomationProperties.SetName(element, name);
    private static C.TextBlock IconLabel(string glyph) => new() { Text = glyph, FontFamily = new("Segoe UI Symbol"), FontSize = 18,
        MinWidth = 20, TextAlignment = W.TextAlignment.Center, VerticalAlignment = W.VerticalAlignment.Center, Margin = new(0, 0, 3, 0) };
    private static C.ScrollViewer Scroll(C.StackPanel content, double max) => new() { Content = content, MaxHeight = max,
        VerticalScrollBarVisibility = C.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = C.ScrollBarVisibility.Disabled };
    private void ClosePanel(object sender, W.RoutedEventArgs e) => DismissPanel();
    private void DismissPanel()
    {
        if (IconBrowser.Visibility == W.Visibility.Visible) LeaveIconBrowser();
        else Close();
    }
    private void Commit(Action action)
    {
        try { action(); if (IsLoaded) Close(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or InvalidDataException or System.Text.Json.JsonException)
        { Error(e.Message); }
    }
    private void Error(string message)
    {
        ErrorMessage.Text = message;
        ErrorMessage.Visibility = message.Length == 0 ? W.Visibility.Collapsed : W.Visibility.Visible;
    }
}
