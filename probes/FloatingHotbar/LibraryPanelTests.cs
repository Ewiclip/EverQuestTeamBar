using W = System.Windows;
using C = System.Windows.Controls;
using I = System.Windows.Input;
using M = System.Windows.Media;

namespace TeamBar;

internal static class LibraryPanelTests
{
    internal static void Run(string output, string sourceProfile)
    {
        string folder = Path.GetFullPath(output) + ".fixtures";
        if (File.Exists(output) || Directory.Exists(folder)) throw new IOException("Use a new library-panel report.");
        Directory.CreateDirectory(folder);
        using var access = GameDataAccess.FixtureScope(false);
        var source = TeamEditing.Load(sourceProfile);
        var buttons = StandaloneButtons.Load(sourceProfile).Take(7).ToArray();
        if (buttons.Length != 7) throw new InvalidDataException("This visual fixture requires seven existing library buttons.");
        byte[] profileBytes = File.ReadAllBytes(sourceProfile);
        string profile = Path.Combine(folder, "team.json");
        var groups = Enumerable.Range(1, 7).Select(i => new ButtonGroup("panel-group-" + i, "Layout fixture " + i,
            [new(buttons[(i - 1) % buttons.Length].Value.Id, 0)], 2)).ToArray();
        TeamEditing.Save(profile, source with { Buttons = [], Groups = groups, FollowCharacterId = null }, []);
        foreach (var button in buttons) StandaloneButtons.Save(StandaloneButtons.PathFor(profile, button.Value.Id), button.Value, null);
        var checks = new List<string>();
        void Check(bool ok, string why) { if (!ok) throw new InvalidOperationException(why); checks.Add(why); }
        var setup = new SetupWindow(profile, "no-input-fixture", () => new([], []),
            userSettings: new MemoryUserSettingsStore(new() { Theme = AppearanceTheme.Dark }));
        try { setup.TestLibraryPanel(folder, Check); }
        finally { setup.CloseUsabilityFixture(); }
        Check(profileBytes.SequenceEqual(File.ReadAllBytes(sourceProfile)) && buttons.All(b => b.Bytes.SequenceEqual(File.ReadAllBytes(b.Path))),
            "The source library and its saved button files remain byte-for-byte unchanged.");
        CollectorFormat.WriteNew(output, new { passed = true, tests = checks, panelWidth = 240, panelHeight = 455,
            sourceNames = buttons.Select(b => b.Value.Label), fixtureGroupsOnly = true, screenshots = folder, gameInputSent = false, gameMemoryRead = false });
    }
}

public partial class SetupWindow
{
    internal void TestLibraryPanel(string folder, Action<bool, string> check)
    {
        SynchronizationContext.SetSynchronizationContext(new W.Threading.DispatcherSynchronizationContext(Dispatcher));
        Height = 1000; Width = 1200; LibraryPanel.Width = 240; LibraryPanel.Height = 455; LibraryPanel.VerticalAlignment = W.VerticalAlignment.Top;
        Show(); Settle();
        long until = Environment.TickCount64 + 5000;
        while (refreshing && Environment.TickCount64 < until) { Settle(); Thread.Sleep(5); }
        StandaloneLibraryMode.IsChecked = true; LibrarySearch.Clear(); Settle();
        C.ListBoxItem Row(int i) => (C.ListBoxItem)ButtonList.ItemContainerGenerator.ContainerFromIndex(i);
        W.Rect Bounds(W.FrameworkElement e) => e.TransformToAncestor(LibraryPanel).TransformBounds(new(0, 0, e.ActualWidth, e.ActualHeight));
        C.ScrollViewer ListScroll() => SettingsDescendants(ButtonList).OfType<C.ScrollViewer>().First();
        void Shot(string name) { Settle(); ExportLibraryPanel(Path.Combine(folder, name + ".png")); }
        void Click(C.Button button) => button.RaiseEvent(new W.RoutedEventArgs(C.Button.ClickEvent));
        C.Button RemoveFor(object item)
        {
            ButtonList.ScrollIntoView(item); Settle();
            var row = (C.ListBoxItem)ButtonList.ItemContainerGenerator.ContainerFromItem(item);
            return SettingsDescendants(row).OfType<C.Button>().Single();
        }
        var selectedSource = standaloneDrafts.FirstOrDefault(b => b.Label == "All Invis") ?? standaloneDrafts[0];
        ButtonList.SelectedItem = selectedSource; NameField.Focus(); Settle();
        check(LibraryPanel.ActualWidth == 240 && LibraryPanel.ActualHeight == 455, "The rendered panel is exactly 240 × 455 logical pixels.");
        check(ButtonList.Items.Count == 7 && Enumerable.Range(0, 7).All(i => Math.Abs(Row(i).ActualHeight - 36) < .1)
            && ListScroll().ScrollableHeight == 0, "All seven real library entries fit in single-line 36 px rows without scrolling.");
        check(Math.Abs(StandaloneLibraryMode.ActualWidth - BarLibraryMode.ActualWidth) < .1
            && Math.Abs(DuplicateButtonControl.ActualWidth - AddButtonControl.ActualWidth) < .1
            && Bounds(LibraryTabs).Top == 1 && Bounds(LibraryFooter).Bottom == 454
            && LibraryTabs.ActualHeight == LibraryFooter.ActualHeight, "Equal tabs and footer halves are flush with the panel edges and share a 40 px height.");
        check(!SettingsDescendants(LibraryPanel).OfType<C.TextBlock>().Any(t => t.Text == "YOUR BUTTONS" || t.Text.StartsWith("One library") || t.Text.Contains("always visible")),
            "The library panel contains no heading, count, explanation, or row subtitles.");
        check(StandaloneLibraryMode.Template.FindName("ActiveLine", StandaloneLibraryMode) is C.Border { Visibility: W.Visibility.Visible }
            && BarLibraryMode.Template.FindName("ActiveLine", BarLibraryMode) is C.Border { Visibility: W.Visibility.Collapsed }, "Only the active library tab has an accent underline.");
        Shot("buttons-seven-dark-240x455");
        check(FindName("SimpleMode") is null && FindName("AdvancedMode") is null && FindName("StandaloneVisibility") is null
            && FindName("PreviewList") is null && choices.Any(c => c.Target.Class is not null && c.Target.CharacterId is null)
            && RuleList.IsEnabled, "One button editor exposes class recipients without mode, visibility, or send-preview sections.");
        RenderSettings(Path.Combine(folder, "button-editor-dark.png"));
        ThemePalette.Apply(Resources, AppearanceTheme.Light); Shot("buttons-seven-light-240x455"); ThemePalette.Apply(Resources, AppearanceTheme.Dark);

        LibrarySearch.Text = "definitely no library match"; Settle();
        check(ButtonList.Items.Count == 0 && !DuplicateButtonControl.IsEnabled && AddButtonControl.IsEnabled
            && LibraryEmptyHint.IsVisible && EmptyHeading.Text == "No matches", "An empty button search keeps search and Add reachable, disables Duplicate, and explains the result.");
        Shot("buttons-no-results-240x455"); Click(ClearLibrarySearch); Settle();
        check(ButtonList.Items.Count == 7 && LibrarySearchPlaceholder.Text == "Search buttons…", "Clearing search restores the actual entries and button-specific placeholder.");

        ButtonList.SelectedIndex = 0; Settle(); Row(0).Focus(); Settle();
        check(Row(0).IsKeyboardFocusWithin && Row(0).Template.FindName("Focus", Row(0)) is C.Border { Visibility: W.Visibility.Visible },
            "Keyboard focus has a visible outline separate from the selection stripe.");
        Row(0).RaiseEvent(new I.KeyEventArgs(I.Keyboard.PrimaryDevice, W.PresentationSource.FromVisual(ButtonList), 0, I.Key.Down) { RoutedEvent = I.Keyboard.KeyDownEvent }); Settle();
        check(ButtonList.SelectedIndex == 1, "Down Arrow moves keyboard selection to the next compact row.");
        Shot("buttons-keyboard-focus-240x455");
        DuplicateButtonControl.Focus(); Settle();
        check(DuplicateButtonControl.Template.FindName("Focus", DuplicateButtonControl) is C.Border { Visibility: W.Visibility.Visible }, "Footer keyboard focus is visibly outlined.");
        StandaloneLibraryMode.Focus(); Settle();
        check(StandaloneLibraryMode.Template.FindName("Focus", StandaloneLibraryMode) is C.Border { Visibility: W.Visibility.Visible }, "Tab keyboard focus is visibly outlined.");

        ButtonList.SelectedItem = selectedSource;
        var sourceDocument = DocumentFor(selectedSource);
        Click(DuplicateButtonControl); Settle(); var copy = selected!;
        check(copy.Id != selectedSource.Id && copy.Rules.SequenceEqual(sourceDocument.Rules) && copy.ShowFor.SequenceEqual(sourceDocument.ShowFor)
            && !copy.Floating && FlushAutoSave(), "The footer duplicates a button with its mappings and visibility preserved and floating display off.");
        ButtonList.SelectedItem = selectedSource; Settle();
        DeleteRequest? asked = null; ConfirmDeletion = (_, request) => { asked = request; return false; };
        Click(RemoveFor(copy)); Settle();
        check(asked?.Name == copy.Label && standaloneDrafts.Contains(copy), "The compact row X opens confirmation for its own entry; Cancel preserves it.");
        ConfirmDeletion = (_, _) => true; Click(RemoveFor(copy)); FlushAutoSave();
        check(!standaloneDrafts.Contains(copy) && standaloneDrafts.Contains(selectedSource), "Confirmed removal deletes only the clicked copy and preserves the selected original.");
        ConfirmDeletion = DeleteConfirmation.Ask;
        Click(AddButtonControl); Settle(); var added = selected!;
        check(added.Label == "" && added.ManagementName == "Unnamed button" && !DuplicateButtonControl.IsEnabled && NameField.IsKeyboardFocused
            && ButtonValidationMessage.IsVisible && ButtonValidationMessage.Text.Contains("Add a keypress") && !SaveControl.IsEnabled,
            "Add opens a new editable draft with an actionable validation error; incomplete drafts cannot be saved or duplicated.");
        added.Rules = [.. selectedSource.Rules]; LoadButton(); MarkChanged();
        check(FlushAutoSave() && DuplicateButtonControl.IsEnabled && !ButtonValidationMessage.IsVisible
            && StandaloneButtons.Load(profilePath).Single(b => b.Value.Id == added.Id).Value.Label == "",
            "A new button with a shortcut can be saved and duplicated with its name left blank.");
        Click(DuplicateButtonControl); Settle(); var blankCopy = selected!;
        check(blankCopy.Id != added.Id && blankCopy.Label == "" && FlushAutoSave(),
            "Duplicating an unnamed button keeps the label blank and assigns a distinct ID.");
        ConfirmDeletion = (_, _) => true; Click(RemoveFor(blankCopy)); FlushAutoSave();
        Click(RemoveFor(added)); FlushAutoSave(); ConfirmDeletion = DeleteConfirmation.Ask;

        ButtonList.SelectedItem = selectedSource; string name = selectedSource.Label;
        NameField.Text = new string('W', 24); Settle();
        var label = SettingsDescendants(Row(ButtonList.SelectedIndex)).OfType<C.TextBlock>().Single(t => t.ToolTip as string == selectedSource.Label);
        var formatted = new M.FormattedText(label.Text, System.Globalization.CultureInfo.InvariantCulture, W.FlowDirection.LeftToRight,
            new M.Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch), label.FontSize, M.Brushes.White, 1);
        check(label.TextTrimming == W.TextTrimming.CharacterEllipsis && formatted.Width > label.ActualWidth
            && label.ToolTip as string == new string('W', 24) && Row(ButtonList.SelectedIndex).ActualHeight == 36,
            "Long button names ellipsize on one line, retain their complete tooltip, and leave the X available.");
        Shot("buttons-long-name-240x455"); NameField.Text = name; FlushAutoSave();

        BarLibraryMode.IsChecked = true; Settle();
        check(LibrarySearchPlaceholder.Text == "Search groups…" && (string)AddButtonControl.Content == "+ Add group"
            && ButtonList.Items.Count == 7 && DuplicateButtonControl.IsEnabled, "Groups uses the same fixed layout and context-specific search and footer actions.");
        Shot("groups-seven-240x455");
        var group = selectedGroup!; int libraryCount = standaloneDrafts.Count;
        Click(DuplicateButtonControl); Settle(); var groupCopy = selectedGroup!;
        check(groupCopy.Id != group.Id && groupCopy.Buttons.SequenceEqual(group.Buttons) && groupCopy.Columns == group.Columns
            && standaloneDrafts.Count == libraryCount && FlushAutoSave(), "Duplicating a group preserves its layout and shares the existing saved buttons without copying their definitions.");
        check(groupDrafts.Count == 8 && AddButtonControl.IsEnabled && DuplicateButtonControl.IsEnabled,
            "The group footer keeps Add and Duplicate available across larger libraries.");
        ConfirmDeletion = (_, _) => true; Click(RemoveFor(groupCopy)); FlushAutoSave(); ConfirmDeletion = DeleteConfirmation.Ask;
        Click(AddButtonControl); Settle(); var newGroup = selectedGroup!;
        check(groupDrafts.Count == 8 && newGroup.Name == "Button group" && GroupEditor.IsVisible, "The group footer Add creates and selects a new group in its editor.");
        groupName!.Text = "Emergency recovery and support utilities"; Settle(); Shot("groups-long-name-240x455"); FlushAutoSave();
        LibrarySearch.Text = "no matching group"; Settle();
        check(ButtonList.Items.Count == 0 && !DuplicateButtonControl.IsEnabled && ClearLibrarySearch.IsVisible, "Group search handles no matches without losing the fixed controls.");
        Shot("groups-no-results-240x455"); Click(ClearLibrarySearch); Settle();
        LibraryPanel.Height = 310; Settle();
        var groupFooter = Bounds(LibraryFooter); var groupTabs = Bounds(LibraryTabs); ListScroll().ScrollToEnd(); Settle();
        check(ListScroll().ScrollableHeight > 0 && groupFooter == Bounds(LibraryFooter) && groupTabs == Bounds(LibraryTabs),
            "At a shorter window height, only group rows scroll; both navigation and footer stay fixed.");
        LibraryPanel.Height = 455; Settle();

        StandaloneLibraryMode.IsChecked = true; LibrarySearch.Clear();
        for (int i = 0; i < 13; i++) { ButtonList.SelectedItem = selectedSource; Click(DuplicateButtonControl); }
        FlushAutoSave(); Settle();
        var tabs = Bounds(LibraryTabs); var search = Bounds(LibrarySearchRow); var footer = Bounds(LibraryFooter);
        ListScroll().ScrollToTop(); Settle(); Shot("buttons-overflow-top-240x455");
        ListScroll().ScrollToEnd(); Settle();
        check(standaloneDrafts.Count == 20 && ListScroll().ScrollableHeight > 0 && ListScroll().VerticalOffset > 0
            && Bounds(LibraryTabs) == tabs && Bounds(LibrarySearchRow) == search && Bounds(LibraryFooter) == footer,
            "With twenty buttons at 240 × 455, scrolling moves only rows while tabs, search, and footer remain in place.");
        Shot("buttons-overflow-bottom-240x455");
        RenderSettings(Path.Combine(folder, "full-editor-dark.png"));
        check(!Enum.GetValues<MemoryFeature>().Any(GameDataAccess.IsEnabled), "Panel verification never enables game-data memory reading.");
    }
    private void ExportLibraryPanel(string path)
    {
        var bitmap = new M.Imaging.RenderTargetBitmap((int)LibraryPanel.ActualWidth, (int)LibraryPanel.ActualHeight, 96, 96, M.PixelFormats.Pbgra32);
        // Render local bounds: rendering the arranged panel directly also includes its parent-grid offset.
        var visual = new M.DrawingVisual();
        using (var drawing = visual.RenderOpen())
            drawing.DrawRectangle(new M.VisualBrush(LibraryPanel), null, new W.Rect(0, 0, LibraryPanel.ActualWidth, LibraryPanel.ActualHeight));
        bitmap.Render(visual); var encoder = new M.Imaging.PngBitmapEncoder(); encoder.Frames.Add(M.Imaging.BitmapFrame.Create(bitmap));
        using var file = new FileStream(path, FileMode.CreateNew); encoder.Save(file);
    }
}
