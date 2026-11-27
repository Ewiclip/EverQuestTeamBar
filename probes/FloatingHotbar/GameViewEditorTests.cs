using C = System.Windows.Controls;
using W = System.Windows;
using System.Windows.Threading;
namespace TeamBar;

internal static class GameViewEditorTests
{
    internal static void Run(string output)
    {
        string folder = Path.GetFullPath(output) + ".fixtures";
        if (File.Exists(output) || Directory.Exists(folder)) throw new IOException("Use a new game view editor test output.");
        Directory.CreateDirectory(folder);
        using var access = GameDataAccess.FixtureScope(false);
        var checks = new List<string>();
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); checks.Add(message); }
        var app = new W.Application { ShutdownMode = W.ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        string profile = Path.Combine(folder, "team.json");
        var team = new TeamDefinition(1, [new("one", "Paladin"), new("two", "Cleric"), new("three", "Bard")], [], []);
        TeamEditing.Save(profile, team, []);
        CheckInlineAssignments(Check);
        // The editor exercises Apply against its own profile. A real Team Bar
        // can be open on the same desktop with a live recovery journal.
        GameViewPlacements.EditorFixtureDirectory = folder;
        try { TeamBarApplication.TestGameViewEditor(app, profile, Check); }
        finally { GameViewPlacements.EditorFixtureDirectory = null; }
        CollectorFormat.WriteNew(output, new { passed = true, checks, gameInputSent = false });
    }

    private static void CheckInlineAssignments(Action<bool, string> check)
    {
        var left = new GameViewMonitor("Left", new(-1600, 0, 1600, 900), new(-1600, 0, 1600, 860), false, "left-panel");
        var right = new GameViewMonitor("Right", new(0, 0, 1920, 1080), new(0, 0, 1920, 1040), true, "right-panel");
        var paladin = new GameViewWindow(10, 100, 20);
        var cleric = new GameViewWindow(11, 110, 21);
        var manual = new GameViewWindow(12, 120, 22);
        var original = new GameViewLayout("Two panels", GameViewKind.EqualSplit, left.Device, true,
            ["Paladin", "Bard", "Cleric", ""])
        {
            Screens = [new(left.Device, GameViewKind.EqualSplit, 2, MonitorId: left.MonitorId),
                new(right.Device, GameViewKind.Rows, 2, MonitorId: right.MonitorId)],
            MasterSlot = 1, Windows = [paladin, null, cleric, manual], AlwaysOnTop = true, AvoidTaskbar = true
        };
        var draft = new GameViewInlineDraft(original, [right, left]);
        var leftPlan = draft.Screens.Single(s => s.Device == left.Device);
        var rightPlan = draft.Screens.Single(s => s.Device == right.Device);
        draft.Assign(rightPlan, 0, "PALADIN");
        var moved = draft.Build(original.Name);
        check(moved.Characters[0] == "" && moved.Characters[1] == "Bard" &&
            moved.Characters[2].Equals("Paladin", StringComparison.OrdinalIgnoreCase) && moved.Characters[3] == "" &&
            moved.Windows!.SequenceEqual([null, null, paladin, manual]) && moved.MasterIndex == 1 &&
            moved.AlwaysOnTop && moved.AvoidTaskbar && moved.ScreenPlans.SequenceEqual(original.ScreenPlans),
            "Assigning a character across monitors moves their manual window too, evicts the destination occupant, and preserves unrelated seats and the star.");
        draft.Assign(leftPlan, 1, "Paladin", manual);
        var reconciled = draft.Build(original.Name);
        check(reconciled.Characters.SequenceEqual(["", "Paladin", "", ""]) &&
            reconciled.Windows!.SequenceEqual([null, manual, null, null]) &&
            original.Characters.SequenceEqual(["Paladin", "Bard", "Cleric", ""]) &&
            original.Windows!.SequenceEqual([paladin, null, cleric, manual]),
            "An incoming character and window can originate in two separate seats. Both are cleared, the replacement is unique, and the saved model is untouched.");
        draft.Assign(leftPlan, 0, null, manual);
        var byWindow = draft.Build(original.Name);
        check(byWindow.Characters.SequenceEqual(["Paladin", "", "", ""]) &&
            byWindow.Windows!.SequenceEqual([manual, null, null, null]),
            "Moving a manually identified window keeps its character label when screen detection is off.");
        draft.SetMaster(rightPlan, 1);
        draft.Clear(leftPlan, 0);
        var empty = draft.Build(original.Name);
        check(empty.Characters.All(string.IsNullOrEmpty) && empty.Windows is null && empty.MasterIndex == 3 &&
            empty.ScreenPlans.SequenceEqual(original.ScreenPlans),
            "Clearing an assignment releases both identities and keeps the starred seat, even when all seats are empty.");
        draft.AddSwapButton = true;
        draft.SwapOnClick = false;
        draft.SetMaster(leftPlan, 0);
        check(draft.Build(original.Name) is { AddSwapButton: true, SwapOnClick: false, SwapTargetIndex: 0 } &&
            draft.PreviewSlots(leftPlan)[0].SwapTarget,
            "Moving the star in button-only mode keeps ordinary clicks independent of swaps.");

        var liveCharacters = new Dictionary<GameViewWindow, string> { [manual] = "Bard" };
        GameViewLayout MoveDetected(string oldName, string movedName)
        {
            var saved = original with { Characters = [oldName, "", "", ""], Windows = [manual, null, null, null] };
            var edit = new GameViewInlineDraft(saved, [left, right]);
            edit.Assign(edit.Screens.Single(s => s.Device == right.Device), 0, movedName, liveCharacters: liveCharacters);
            return edit.Build(saved.Name);
        }
        var identified = MoveDetected("", "bard");
        check(identified.Characters[0] == "" && identified.Characters[2].Equals("Bard", StringComparison.OrdinalIgnoreCase) &&
            identified.Windows is null, "A detected character's name chip releases the same live window from a manually assigned unnamed seat.");
        var relogged = MoveDetected("Paladin", "Bard");
        check(relogged.Characters.SequenceEqual(["Paladin", "", "Bard", ""]) && relogged.Windows is null,
            "After a player changes characters in the same window, moving its current character leaves the other character's named seat available.");
        var offlineName = MoveDetected("Paladin", "Paladin");
        check(offlineName.Characters.SequenceEqual(["", "", "Paladin", ""]) && offlineName.Windows is null,
            "Moving an offline name never carries a stale binding to a window now known to be running another character.");
    }
}

internal sealed partial class TeamBarApplication
{
    internal static void TestGameViewEditor(W.Application app, string profile, Action<bool, string> check)
    {
        var store = new MemoryUserSettingsStore();
        GameSession[] windows = [];
        DetectedClient[] roster = [];
        var fullscreen = new HashSet<GameViewWindow>();
        bool refuseScan = false;
        using var host = new TeamBarApplication(app, (file, controller) =>
            new SetupWindow(file, controller, () => new(roster, []), userSettings: store,
                windowProvider: () => refuseScan ? throw new InvalidOperationException("Fixture window unavailable.") : windows,
                requiresWindowedMode: window => fullscreen.Contains(GameViewWindow.From(window)))
                { ShowActivated = false }, store);
        Current = host;
        const string controller = "offline-fixture";
        try
        {
            host.ShowSetup(profile, controller);
            var setup = host.OpenSetupForProfile(profile)!;
            void ExerciseDialog(Action<GameViewLayoutWindow> actions, Action? open = null)
            {
                Exception? failure = null;
                bool found = false;
                setup.Dispatcher.BeginInvoke(() =>
                {
                    var dialog = setup.OwnedWindows.OfType<GameViewLayoutWindow>().SingleOrDefault();
                    if (dialog is null) return;
                    found = true;
                    try { actions(dialog); }
                    catch (Exception ex) { failure = ex; }
                    finally { if (dialog.IsVisible) dialog.Close(); }
                }, DispatcherPriority.ApplicationIdle);
                if (open is null) setup.GameViewLayoutsButton.RaiseEvent(new W.RoutedEventArgs(C.Primitives.ButtonBase.ClickEvent));
                else open();
                if (failure is not null) throw new InvalidOperationException("Layout editor UI check failed.", failure);
                if (!found) throw new InvalidOperationException("Game windows did not open the layout editor.");
            }
            static void Click(C.Button button) => button.RaiseEvent(new W.RoutedEventArgs(C.Primitives.ButtonBase.ClickEvent));
            static void WaitForWindowPoll(Func<bool> complete)
            {
                var frame = new DispatcherFrame();
                var deadline = Environment.TickCount64 + 6500;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                timer.Tick += (_, _) =>
                {
                    if (complete() || Environment.TickCount64 >= deadline) frame.Continue = false;
                };
                timer.Start();
                try { Dispatcher.PushFrame(frame); }
                finally { timer.Stop(); }
            }
            static void Choose(C.ComboBox picker, string label) =>
                picker.SelectedItem = picker.Items.Cast<object>().Single(x => x.ToString() == label);
            static void SelectLayout(GameViewLayoutWindow dialog, string name) =>
                dialog.SavedLayouts.SelectedItem = dialog.SavedLayouts.Items.OfType<GameViewLayout>().Single(l => l.Name == name);
            void OpenScreens()
            {
                setup.ScreensTab.IsChecked = true;
                // WPF attaches the newly visible ScrollViewer's contents during layout.
                setup.UpdateLayout();
            }

            OpenScreens();
            check(setup.ScreensTab.IsEnabled && setup.ScreensView.IsVisible && !setup.ReviewRoot.IsVisible &&
                setup.NewScreenLayout.IsEnabled && setup.EmptyScreenLayouts.IsVisible &&
                !setup.ApplyScreenLayout.IsVisible && !setup.EndScreenLayout.IsVisible && !setup.SettingsFooter.IsVisible,
                $"The main Screens tab offers a new layout with no saved presets and memory reading off. " +
                $"Panel={setup.ScreensView.IsVisible}, new enabled={setup.NewScreenLayout.IsEnabled}, " +
                $"empty={setup.EmptyScreenLayouts.IsVisible}, apply={setup.ApplyScreenLayout.IsVisible}, " +
                $"end={setup.EndScreenLayout.IsVisible}, footer={setup.SettingsFooter.IsVisible}, feedback={setup.ScreenActionFeedback.Text}");
            setup.OpenAppSettings();
            check(setup.GeneralSettingsView.IsVisible && setup.ScreensTab.IsChecked == false && !setup.ScreensView.IsVisible,
                "Opening Settings from Screens shows general preferences and clears the active tab.");
            OpenScreens();
            check(setup.ScreensView.IsVisible && !setup.GeneralSettingsView.IsVisible && !setup.SettingsFooter.IsVisible,
                "Selecting Screens from Settings opens the layouts directly.");
            setup.ShowHotKeys();

            ExerciseDialog(dialog =>
            {
                check(Equals(dialog.Capacity.SelectedItem, 0) && dialog.EmptyScreenHint.IsVisible && dialog.SwapOnClick.IsChecked == false,
                    "A new layout shows the monitor map with no slots reserved.");
                check(dialog.AlwaysOnTop.IsChecked == false, "New layouts start with ordinary window priority.");
                check(dialog.HideTitleBars.IsChecked == false, "New layouts keep the game title bars visible.");
                check(dialog.MatchMasterResolution.IsChecked == true, "The wizard defaults to matching the master resolution.");
                dialog.Capacity.SelectedItem = 1;
                check(dialog.Diagram.Children.Count == 1 && dialog.Arrangement.Items.Count == 1 && !dialog.SwapOnClick.IsEnabled,
                    "A solo layout has a single full-monitor slot.");
                dialog.Capacity.SelectedItem = 4;
                Choose(dialog.Arrangement, "Grid");
                dialog.LayoutName.Text = "Grid";
                dialog.OverlapTaskbar.IsChecked = false;
                dialog.AlwaysOnTop.IsChecked = true;
                dialog.HideTitleBars.IsChecked = true;
                dialog.MatchMasterResolution.IsChecked = false;
                check(dialog.SwapRedrawNotice.Visibility == W.Visibility.Collapsed,
                    "The wizard hides the redraw note for fixed slots at their own resolutions.");
                Click((C.Button)dialog.Diagram.Children[2]);
                check(dialog.AssignStep.IsChecked == true && dialog.SlotRows[2].Character.Items.Contains("Bard"),
                    "Selecting a numbered preview tile opens assignments and offers saved characters with EQ closed.");
                dialog.SlotRows[0].Character.Text = "Paladin";
                dialog.SlotRows[2].Character.Text = "Bard";
                Click(dialog.SaveLayoutButton);
                var grid = GameViewLayouts.Load(profile).Single();
                check(grid.AlwaysOnTop && !grid.MatchMasterResolution && grid.HideTitleBars,
                    "The wizard saves priority, resolution and title bar visibility independently of swapping.");
                check(grid.Kind == GameViewKind.Grid && grid.AvoidTaskbar && !grid.SwapOnClick &&
                    grid.Characters.SequenceEqual(["Paladin", "", "Bard", ""]) && dialog.Diagram.Children.Count == 4,
                    "A four-slot grid saves two named assignments in their original positions and preserves both gaps.");
                Click(dialog.SlotRows[2].Clear);
                check(dialog.SlotRows[2].Character.Text.Length == 0 && dialog.SlotRows[0].Character.Text == "Paladin",
                    "Clearing a slot leaves every other character in place.");
                Click(dialog.NewLayout);
                dialog.Capacity.SelectedItem = 6;
                Choose(dialog.Arrangement, "Main + preview strip");
                check(dialog.SwapOnClick.IsChecked == false, "Choosing an arrangement leaves the global swap policy alone.");
                check(dialog.MasterResolutionClickNotice.IsVisible && dialog.MasterResolutionClickNotice.Text == GameViewLayoutWindow.FixedPreviewClickHint,
                    "The wizard explains fixed-preview clicks for this game installation when Quick Swap is off.");
                dialog.SwapOnClick.IsChecked = true;
                dialog.MatchMasterResolution.IsChecked = false;
                check(dialog.SwapRedrawNotice.IsVisible && dialog.MasterResolutionClickNotice.Visibility == W.Visibility.Collapsed,
                    "The wizard shows only the redraw note when swapping at each slot's resolution.");
                dialog.MatchMasterResolution.IsChecked = true;
                check(dialog.SwapRedrawNotice.Visibility == W.Visibility.Collapsed && dialog.MasterResolutionClickNotice.IsVisible &&
                    dialog.MasterResolutionClickNotice.Text.Contains("swap that character"),
                    "The wizard explains that a preview click promotes the character when Quick Swap is on.");
                dialog.AddSwapButton.IsChecked = true;
                dialog.SwapOnClick.IsChecked = false;
                check(dialog.MasterOptions.IsVisible && dialog.ButtonAppearance.IsVisible &&
                    dialog.ButtonAppearance.ButtonStyle == GameViewSwapButtonStyle.CharacterName,
                    "Button-only mode keeps the destination selector and defaults the button to the character name.");
                dialog.ButtonAppearance.StylePicker.SelectedValue = GameViewSwapButtonStyle.Icon;
                dialog.MasterSeat.SelectedItem = 4;
                dialog.Edge.SelectedItem = GameViewEdge.Left;
                dialog.LayoutName.Text = "Six";
                Click(dialog.SaveLayoutButton);
                check(GameViewLayouts.Load(profile).Single(l => l.Name == "Six") is
                    { SwapOnClick: false, AddSwapButton: true, SwapTargetIndex: 3, SwapButtonStyle: GameViewSwapButtonStyle.Icon },
                    "The wizard saves an independently enabled icon button and its chosen destination.");
                dialog.SwapOnClick.IsChecked = true;
                dialog.AddSwapButton.IsChecked = false;
                Click(dialog.SaveLayoutButton);
                var empty = GameViewLayouts.Load(profile).Single(l => l.Name == "Six");
                check(empty.Edge == GameViewEdge.Left && empty.SwapOnClick && empty.Characters.Length == 6 &&
                    empty.Characters.All(string.IsNullOrEmpty) && dialog.Diagram.Children.Count == 6,
                    "Six empty slots can be saved before any character logs in, including the strip edge and swap policy.");
                Click(dialog.ApplyLayoutButton);
                check(host.ActiveGameViewName == "Six", $"Applying an empty layout keeps it active for later assignments. {dialog.Feedback.Text}");
            });

            host.PopulateLayoutMenu();
            var choices = host.layoutMenu.DropDownItems.OfType<System.Windows.Forms.ToolStripMenuItem>().ToArray();
            check(choices.Single(i => i.Text == "Six").Checked && choices.Any(i => i.Text == "Edit layouts…"),
                "The tray lists this profile's saved layouts and checks the active choice.");
            choices.Single(i => i.Text == "Grid").PerformClick();
            check(host.ActiveGameViewName == "Grid" && !setup.IsVisible && setup.OwnedWindows.Count == 0,
                "Clicking a saved layout in the tray switches the active layout with Settings and the wizard closed.");
            var invalid = GameViewLayouts.Load(profile).First() with { Monitor = "disconnected fixture", Screens = null };
            bool failedSwitch = false;
            try { setup.ApplyTrayLayout(host, invalid); }
            catch (InvalidOperationException) { failedSwitch = true; }
            check(failedSwitch && host.ActiveGameViewName == "Grid", "A failed tray switch preserves the running layout.");
            choices.Single(i => i.Text == "Six").PerformClick();
            host.ShowSetup(profile, controller);
            ExerciseDialog(dialog => check(dialog.LayoutName.Text == "Grid" && host.ActiveGameViewName == "Six",
                "Editing a saved layout opens the requested preset while a different layout is active."),
                () => setup.EditGameViewLayouts("Grid"));
            ExerciseDialog(dialog => check(dialog.LayoutName.Text == "" && dialog.SavedLayouts.SelectedItem is null &&
                dialog.DesignStep.IsChecked == true && host.ActiveGameViewName == "Six",
                "Creating a layout opens a blank design and keeps the active preset."),
                () => setup.EditGameViewLayouts(createNew: true));
            ExerciseDialog(dialog =>
            {
                check(dialog.AssignStep.IsChecked == true && dialog.LayoutName.Text == "Six" && dialog.EndLayout.IsVisible,
                    "Opening the active layout goes directly to its assignments.");
                Click(dialog.EndLayout);
                check(host.ActiveGameViewName is null, "An empty active layout can be ended normally.");
                SelectLayout(dialog, "Grid");
                check(dialog.AlwaysOnTop.IsChecked == true && dialog.MatchMasterResolution.IsChecked == false && dialog.HideTitleBars.IsChecked == true,
                    "Reopening a saved layout restores its priority, resolution and title bar switches.");
                check(dialog.SlotRows[0].Character.Text == "Paladin" && dialog.SlotRows[2].Character.Text == "Bard" &&
                    dialog.OverlapTaskbar.IsChecked == false, "Reopening the grid restores its assigned seats, gaps and work area.");
                dialog.SlotRows[1].Character.Text = "paladin";
                Click(dialog.SaveLayoutButton);
                check(!string.IsNullOrEmpty(dialog.Feedback.Text) && GameViewLayouts.Load(profile).Single(l => l.Name == "Grid").Characters[1] == "",
                    "A duplicate name is rejected without overwriting a saved empty slot.");
            });
            setup.Close();
            check(!setup.IsVisible && ReferenceEquals(setup, host.OpenSetupForProfile(profile)), "Closing Settings still hides it in the tray.");
            host.ShowSetup(profile, controller);
            check(setup.IsVisible && ReferenceEquals(setup, host.OpenSetupForProfile(profile)), "Opening Settings still reuses the existing window.");

            OpenScreens();
            setup.ScreenLayoutPicker.SelectedItem = setup.ScreenLayoutPicker.Items.OfType<GameViewLayout>().Single(layout => layout.Name == "Grid");
            check(setup.ScreenLayoutDescription.Text.StartsWith("Grid") &&
                setup.ScreenAlwaysOnTopSwitch.IsChecked == true && setup.ScreenOverlapTaskbarSwitch.IsChecked == false &&
                setup.ScreenSwappingSwitch.IsChecked == false && setup.ScreenMatchMasterResolutionSwitch.IsChecked == false && setup.ScreenHideTitleBarsSwitch.IsChecked == true &&
                setup.ScreenLayoutSlots.Text.Split(Environment.NewLine).Length == 4 && setup.ScreenLayoutSlots.Text.Contains("3   Bard"),
                "Screens shows the selected preset's switches and assigned and empty seats.");
            setup.UpdateLayout();
            var gridCells = Descendants(setup.SavedScreenMap).OfType<C.Border>().Where(b => b.Tag is GameViewMapSlot).ToArray();
            check(gridCells.Length == 4 && gridCells[2].Tag is GameViewMapSlot { Label: "Bard" } &&
                !Descendants(setup.SavedScreenMap).OfType<W.Shapes.Path>().Any() &&
                setup.SavedScreenMap.Children.OfType<C.Button>().All(tile => !tile.Focusable && !tile.IsTabStop),
                "The saved fixed preset previews all four seats and their assignments in a read-only map without a swap star.");
            var six = setup.ScreenLayoutPicker.Items.OfType<GameViewLayout>().Single(l => l.Name == "Six");
            setup.ScreenLayoutPicker.SelectedItem = six;
            setup.UpdateLayout();
            check(setup.ScreenMasterResolutionClickNotice.IsVisible && setup.ScreenMasterResolutionClickNotice.Text.Contains("swap that character"),
                "Selecting a matching-resolution Quick Swap preset in Screens explains that clicking promotes a character.");
            var sixCells = Descendants(setup.SavedScreenMap).OfType<C.Border>().Where(b => b.Tag is GameViewMapSlot).ToArray();
            check(sixCells.Length == 6 && six.MasterIndex == 3 && OnlyMapStar(setup.SavedScreenMap, sixCells[3]),
                "Selecting the six-slot swapping preset draws one yellow star on its explicitly chosen fourth slot.");
            var savedTile = setup.SavedScreenMap.Children.OfType<C.Button>().Single(b => sixCells[0].IsDescendantOf(b));
            CheckProjection(setup.SavedScreenMap, (GameViewMonitor)savedTile.Tag, six.Kind, six.Characters.Length,
                "The saved-preset overview uses the same monitor edges and strip placements as the launcher.", six.AvoidTaskbar, six.Edge);
            setup.ScreenSwappingSwitch.IsChecked = false;
            check(setup.ScreenMasterResolutionClickNotice.IsVisible && setup.ScreenMasterResolutionClickNotice.Text == GameViewLayoutWindow.FixedPreviewClickHint,
                "Turning off Quick Swap immediately switches the Screens hint to fixed-preview interaction.");
            setup.ScreenMatchMasterResolutionSwitch.IsChecked = false;
            setup.ScreenOverlapTaskbarSwitch.IsChecked = false;
            setup.ScreenAlwaysOnTopSwitch.IsChecked = true;
            check(setup.ScreenSwapRedrawHint.Visibility == W.Visibility.Collapsed && setup.ScreenMasterResolutionClickNotice.Visibility == W.Visibility.Collapsed,
                "The saved layout hides both notices when Quick Swap and resolution matching are off.");
            var updatedSix = GameViewLayouts.Load(profile).Single(layout => layout.Name == "Six");
            check(!updatedSix.SwapOnClick && updatedSix.MasterSlot == 3 && updatedSix.AvoidTaskbar &&
                updatedSix.AlwaysOnTop && !updatedSix.MatchMasterResolution && !Descendants(setup.SavedScreenMap).OfType<W.Shapes.Path>().Any(),
                "Changing the overview switches saves the six-slot preset and clears its active star.");
            setup.ScreenAddSwapButton.IsChecked = true;
            setup.UpdateLayout();
            check(setup.ScreenButtonAppearance.IsVisible && setup.ScreenButtonAppearance.ButtonStyle == GameViewSwapButtonStyle.Icon &&
                setup.ScreenSwappingSwitch.IsChecked == false && setup.ScreenSwapRedrawHint.IsVisible &&
                Descendants(setup.SavedScreenMap).OfType<W.Shapes.Path>().Count() == 1,
                "A saved layout restores the button appearance and shows its star and redraw note without enabling Quick Swap.");
            setup.ScreenButtonAppearance.StylePicker.SelectedValue = GameViewSwapButtonStyle.CustomText;
            var textField = setup.ScreenButtonAppearance.LabelField;
            textField.Text = "Play {character}";
            textField.RaiseEvent(new W.Input.KeyEventArgs(W.Input.Keyboard.PrimaryDevice, W.PresentationSource.FromVisual(textField), 0, W.Input.Key.Enter)
                { RoutedEvent = W.Input.Keyboard.KeyDownEvent });
            check(GameViewLayouts.Load(profile).Single(layout => layout.Name == "Six").SwapButtonLabel("Cleric") == "Play Cleric",
                "Entering custom text in the saved-layout overview updates the displayed character label.");
            setup.ScreenAddSwapButton.IsChecked = false;
            check(!setup.ScreenButtonAppearance.IsVisible && !setup.ScreenSwapRedrawHint.IsVisible &&
                GameViewLayouts.Load(profile).Single(l => l.Name == "Six").SwapButtonText == "Play {character}",
                "Turning off buttons hides their controls and remembers their label for next time.");
            setup.ScreenSwappingSwitch.IsChecked = true;
            setup.UpdateLayout();
            check(setup.ScreenSwapRedrawHint.IsVisible, "A saved layout shows the redraw note for per-slot swaps.");
            sixCells = Descendants(setup.SavedScreenMap).OfType<C.Border>().Where(b => b.Tag is GameViewMapSlot).ToArray();
            check(GameViewLayouts.Load(profile).Single(layout => layout.Name == "Six") is { MasterIndex: 3, MatchMasterResolution: false } &&
                OnlyMapStar(setup.SavedScreenMap, sixCells[3]), "The overview restores the yellow star to the same seat when swapping is re-enabled.");
            check(!Descendants(setup.ScreensView).OfType<C.TextBlock>().Any(label => label.IsVisible &&
                label.Text.Contains("Windows main", StringComparison.OrdinalIgnoreCase)),
                "The saved layout overview omits the Windows main designation.");
            GroupEditorTests.Screenshot(setup.SavedScreenLayoutsPanel, Path.Combine(Path.GetDirectoryName(profile)!, "saved-layout-switches.png"));
            setup.ScreenLayoutPicker.SelectedItem = setup.ScreenLayoutPicker.Items.OfType<GameViewLayout>().Single(l => l.Name == "Grid");
            void EditSelected()
            {
                Click(setup.EditScreenLayout);
                setup.UpdateLayout();
            }
            C.Button[] EditableSlots()
            {
                setup.UpdateLayout();
                return Descendants(setup.SavedScreenMap).OfType<C.Button>().Where(b => b.Tag is GameViewMapSlot).ToArray();
            }
            C.Primitives.ToggleButton PaletteChip(GameViewMapToken token) =>
                setup.InlinePalette.Children.OfType<C.Primitives.ToggleButton>().Single(b => Equals(b.Tag, token));
            static void PressSpace(W.UIElement element)
            {
                element.Focus();
                foreach (var keyEvent in new[] { W.Input.Keyboard.KeyDownEvent, W.Input.Keyboard.KeyUpEvent })
                    element.RaiseEvent(new W.Input.KeyEventArgs(W.Input.Keyboard.PrimaryDevice,
                        W.PresentationSource.FromVisual(element), 0, W.Input.Key.Space) { RoutedEvent = keyEvent });
            }
            static W.DragEventArgs DropOn(C.Button slot, W.DataObject data)
            {
                // WPF only exposes the constructor to its own OLE drag target. Supply a real routed
                // event to the slot so the test can exercise drops without moving the desktop mouse.
                var args = (W.DragEventArgs)Activator.CreateInstance(typeof(W.DragEventArgs),
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null,
                    [data, W.DragDropKeyStates.None, W.DragDropEffects.Move, slot, new W.Point()], null)!;
                args.RoutedEvent = W.DragDrop.DropEvent;
                slot.RaiseEvent(args);
                return args;
            }
            static W.DataObject DragData(C.Primitives.ToggleButton chip) => new(typeof(GameViewMapToken), chip.Tag);
            byte[] savedBeforeEdit = File.ReadAllBytes(GameViewLayouts.PathFor(profile));
            EditSelected();
            check(setup.InlineLayoutName.Text == "Grid" && setup.SaveInlineLayout.IsVisible && setup.CancelInlineLayout.IsVisible &&
                !setup.OwnedWindows.OfType<GameViewLayoutWindow>().Any() && EditableSlots().Length == 4,
                "The pencil edits the selected preset directly in Screens and provides a target for each of the four slots.");
            bool ReceivesDrop(C.Button slot, double x, double y)
            {
                var point = slot.TranslatePoint(new W.Point(x, y), setup.SavedScreenMap);
                var hit = setup.SavedScreenMap.InputHitTest(point) as W.DependencyObject;
                return hit is not null && (hit == slot || slot.IsAncestorOf(hit));
            }
            check(EditableSlots().All(slot => ReceivesDrop(slot, slot.ActualWidth / 2, slot.ActualHeight / 2) &&
                ReceivesDrop(slot, 1, 1) && ReceivesDrop(slot, slot.ActualWidth - 1, slot.ActualHeight - 1)),
                "Each slot receives a drop across its visible area, including near the seams between seats.");
            var invalidDrop = DropOn(EditableSlots()[2], new W.DataObject(W.DataFormats.Text, "outside text"));
            check(invalidDrop.Effects == W.DragDropEffects.None && ((GameViewMapSlot)EditableSlots()[2].Tag).Label == "Bard",
                "Dropping unrelated text on a slot preserves its occupant.");
            var drop = DropOn(EditableSlots()[2], DragData(PaletteChip(new("Paladin"))));
            var reassigned = EditableSlots().Select(b => (GameViewMapSlot)b.Tag).ToArray();
            check(drop.Handled && drop.Effects == W.DragDropEffects.Move &&
                reassigned.Select(slot => slot.Label).SequenceEqual(["", "", "Paladin", ""]),
                "Dropping an already assigned character on an occupied slot clears the old slot and replaces its occupant.");
            var destination = EditableSlots()[1];
            check(destination.IsTabStop && destination.Focusable && destination.AllowDrop,
                "An editable slot is both a drop target and a keyboard-accessible button.");
            PressSpace(PaletteChip(new("Bard")));
            PressSpace(EditableSlots()[1]);
            check(((GameViewMapSlot)EditableSlots()[1].Tag).Label == "Bard",
                "Pressing Space on a displaced character and then a slot assigns them without dragging.");
            var starDrop = DropOn(EditableSlots()[3], DragData(PaletteChip(GameViewMapToken.Star)));
            check(setup.ScreenSwapRedrawHint.IsVisible && setup.ScreenMasterResolutionClickNotice.Visibility == W.Visibility.Collapsed,
                "Placing a swap star in the inline editor shows only the per-slot redraw note.");
            setup.ScreenMatchMasterResolutionSwitch.IsChecked = true;
            check(setup.ScreenSwapRedrawHint.Visibility == W.Visibility.Collapsed && setup.ScreenMasterResolutionClickNotice.IsVisible,
                "Matching the master resolution immediately replaces the inline editor's redraw note with the preview click hint.");
            setup.ScreenHideTitleBarsSwitch.IsChecked = false;
            check(starDrop.Handled && setup.ScreenSwappingSwitch.IsChecked == true &&
                EditableSlots().Select(b => ((GameViewMapSlot)b.Tag).SwapTarget).SequenceEqual([false, false, false, true]) &&
                File.ReadAllBytes(GameViewLayouts.PathFor(profile)).SequenceEqual(savedBeforeEdit),
                "Dropping the star enables swapping, marks the chosen empty slot, and keeps all changes in the draft until Save.");
            Click(setup.CancelInlineLayout);
            check(setup.ScreenMatchMasterResolutionSwitch.IsChecked == false && setup.ScreenHideTitleBarsSwitch.IsChecked == true,
                "Cancel restores the saved resolution and title bar preferences.");
            check(File.ReadAllBytes(GameViewLayouts.PathFor(profile)).SequenceEqual(savedBeforeEdit) && EditableSlots().Length == 0 &&
                setup.EditScreenLayout.IsVisible, "Cancel restores the saved map and its read-only slots without writing the draft.");
            EditSelected();
            DropOn(EditableSlots()[2], DragData(PaletteChip(new("Paladin"))));
            DropOn(EditableSlots()[2], DragData(PaletteChip(GameViewMapToken.Star)));
            setup.ScreenMatchMasterResolutionSwitch.IsChecked = true;
            setup.ScreenHideTitleBarsSwitch.IsChecked = false;
            GroupEditorTests.Screenshot((W.FrameworkElement)setup.ScreensView.Content,
                Path.Combine(Path.GetDirectoryName(profile)!, "inline-screen-editor.png"));
            Click(setup.SaveInlineLayout);
            var inlineGrid = GameViewLayouts.Load(profile).Single(l => l.Name == "Grid");
            check(inlineGrid.Characters.SequenceEqual(["", "", "Paladin", ""]) && inlineGrid.MasterIndex == 2 && inlineGrid.MatchMasterResolution && !inlineGrid.HideTitleBars &&
                !setup.SaveInlineLayout.IsVisible && setup.ScreenLayoutSlots.Text.Contains("3   Paladin"),
                "Saving an inline edit persists the replacement and starred slot and redraws the overview.");
            Click(setup.NewScreenLayout);
            setup.UpdateLayout();
            check(setup.InlineLayoutName.Text.Length == 0 && setup.SaveInlineLayout.IsVisible &&
                !setup.OwnedWindows.OfType<GameViewLayoutWindow>().Any(), "New layout opens an empty draft inside the Screens tab.");
            setup.InlineLayoutName.Text = "Screen preset";
            setup.InlineCapacity.SelectedItem = 2;
            Click(setup.SaveInlineLayout);
            check((setup.ScreenLayoutPicker.SelectedItem as GameViewLayout)?.Name == "Screen preset",
                "A layout created inline is immediately selected on Screens.");
            Click(setup.ApplyScreenLayout);
            check(host.ActiveGameViewName == "Screen preset" && setup.EndScreenLayout.IsVisible &&
                setup.ScreenLayoutStatus.Text.Contains("0 of 2"), "Apply on Screens shows the active layout and occupied slot count.");
            refuseScan = true;
            setup.ScreenAlwaysOnTopSwitch.IsChecked = true;
            setup.ScreenMatchMasterResolutionSwitch.IsChecked = false;
            setup.ScreenHideTitleBarsSwitch.IsChecked = true;
            check(host.activeGameView?.Layout is { Name: "Screen preset", AlwaysOnTop: true, MatchMasterResolution: false, HideTitleBars: true } &&
                setup.IsVisible && setup.ScreensView.IsVisible && setup.ScreenAlwaysOnTopSwitch.IsEnabled,
                "Changing window priority, resolution and title bars reuses the active roster and keeps Settings available during a discovery failure.");
            setup.ScreenSwappingSwitch.IsChecked = true;
            var connectedPreset = GameViewLayouts.Load(profile).Single(l => l.Name == "Screen preset");
            GameViewLayouts.Save(profile, connectedPreset with { Monitor = "Unavailable fixture display", Screens = null });
            setup.ScreenAlwaysOnTopSwitch.IsChecked = false;
            check(host.activeGameView?.Layout.AlwaysOnTop == true && setup.ScreenAlwaysOnTopSwitch.IsChecked == true &&
                GameViewLayouts.Load(profile).Single(l => l.Name == "Screen preset").AlwaysOnTop,
                "A rejected live option change restores the switch and the saved preset to the running settings.");
            setup.ScreenSwappingSwitch.IsChecked = false;
            check(host.ActiveGameViewSession is { IsActive: true } && setup.ScreenSwappingSwitch.IsChecked == true &&
                GameViewLayouts.Load(profile).Single(l => l.Name == "Screen preset").SwapOnClick &&
                setup.ScreenActionFeedback.Text.Contains("Unavailable fixture display"),
                "A rejected swap setting keeps the active layout, restores its switch and gives the reason in Screens.");
            refuseScan = false;
            GameViewLayouts.Save(profile, connectedPreset with { SwapOnClick = false });
            Click(setup.ApplyScreenLayout);
            Click(setup.EndScreenLayout);
            check(host.ActiveGameViewName is null && !setup.EndScreenLayout.IsVisible, "End layout on Screens ends the active session.");
            setup.ShowHotKeys();
            check(!setup.ScreensView.IsVisible && setup.EditorBody.IsVisible, "Returning to Hot Keys leaves the Screens page.");

            var first = new GameSession(17, 101, 30, 200, "DISPLAY1");
            var second = new GameSession(19, 201, 50, 0, "DISPLAY1");
            windows = [first];
            ExerciseDialog(dialog =>
            {
                Click(dialog.NewLayout);
                dialog.Capacity.SelectedItem = 3;
                dialog.LayoutName.Text = "Manual";
                Click(dialog.AssignSlots);
                var middle = dialog.SlotRows[1];
                var last = dialog.SlotRows[2];
                check(middle.WindowPicker.Items.Count == 2 && dialog.SlotRows.Take(3).All(r => r.WindowPicker.SelectedIndex == 0),
                    "Opening an EQ window offers it to every slot without silently claiming one.");
                middle.WindowPicker.SelectedIndex = 1;
                middle.Character.Text = "My tank";
                var firstPick = middle.WindowPicker.SelectedItem.ToString();
                windows = [second, first];
                WaitForWindowPoll(() => last.WindowPicker.Items.Count == 3 && setup.TeamCount.Text.Contains("2 unassigned"));
                check(setup.TeamCount.Text.Contains("2 unassigned") && last.WindowPicker.Items.Count == 3,
                    "Both the wizard and Settings discover the newly opened EQ window with memory reading off.");
                check(last.WindowPicker.SelectedIndex == 0 && middle.WindowPicker.SelectedItem.ToString() == firstPick &&
                    middle.Character.Text == "My tank", "The roster refresh keeps the empty seat and preserves the manual window and label.");
                last.WindowPicker.SelectedIndex = 2;
                Click(dialog.SaveLayoutButton);
                var layout = GameViewLayouts.Load(profile).Single(l => l.Name == "Manual");
                check(layout.Characters.SequenceEqual(["", "My tank", ""]) && layout.Resolve(windows, []).SequenceEqual([null, first, second]),
                    "Two explicit windows resolve in their chosen slots with an empty first slot and an unnamed session.");
            });
            ExerciseDialog(dialog =>
            {
                SelectLayout(dialog, "Manual");
                var middle = dialog.SlotRows[1];
                var last = dialog.SlotRows[2];
                check(last.WindowPicker.SelectedItem.ToString()!.Contains("PID 19"), "Reopening restores the exact saved window.");
                windows = [first, second with { StartedUtcTicks = 555 }];
                dialog.RefreshWindows();
                var saved = GameViewLayouts.Load(profile).Single(l => l.Name == "Manual");
                check(last.WindowPicker.SelectedItem.ToString()!.Contains("Closed") && saved.Resolve(windows, [])[2] is null,
                    "A replacement process using the same PID leaves the original slot offline.");
                last.WindowPicker.SelectedIndex = middle.WindowPicker.SelectedIndex;
                Click(dialog.SaveLayoutButton);
                check(!string.IsNullOrEmpty(dialog.Feedback.Text) && GameViewLayouts.Load(profile).Single(l => l.Name == "Manual")
                    .Windows![2] == GameViewWindow.From(second), "A duplicate window pick keeps the saved layout intact.");
            });

            var unrelated = new GameSession(23, 301, 70, 300, "DISPLAY2");
            windows = [first, second, unrelated];
            fullscreen.UnionWith(windows.Select(GameViewWindow.From));
            var beforeWarning = File.ReadAllBytes(GameViewLayouts.PathFor(profile));
            OpenScreens();
            void SelectSaved(string name) => setup.ScreenLayoutPicker.SelectedItem =
                setup.ScreenLayoutPicker.Items.OfType<GameViewLayout>().Single(l => l.Name == name);
            SelectSaved("Manual");
            var warning = setup.ScreenWindowedWarning;
            check(warning.IsVisible && warning.Text.Contains("My tank") && warning.Text.Contains("Slot 3") &&
                !warning.Text.Contains(unrelated.ProcessId.ToString()) && warning.Text.Contains("Switch to Windowed"),
                "Selecting a preset immediately identifies the fullscreen named and unnamed assignments, excluding unrelated windows.");
            SelectSaved("Grid");
            check(!warning.IsVisible, "Changing to a preset with no identified fullscreen assignments clears the previous warning immediately.");
            // A login changes the open-window roster. Its normal background scan
            // can also identify an existing window without another selection.
            roster = [new(second.ProcessId, second.StartedUtcTicks, second.Window, second.Left, new("Paladin", 3, 100), UserAssigned: true)];
            windows = [first, second, unrelated, new GameSession(29, 401, 90, 400, "DISPLAY2")];
            WaitForWindowPoll(() => warning.IsVisible && warning.Text.Contains("Paladin"));
            check(warning.IsVisible && warning.Text.Contains("Paladin") && !warning.Text.Contains("My tank"),
                "The selected layout resolves a character-name assignment against the updated Settings roster.");
            SelectSaved("Manual");
            fullscreen.Remove(GameViewWindow.From(first));
            WaitForWindowPoll(() => warning.IsVisible && !warning.Text.Contains("My tank"));
            check(warning.IsVisible && warning.Text.Contains("Paladin") && !warning.Text.Contains("My tank"),
                "When one game switches to Windowed on the same HWND, Screens keeps only the remaining fullscreen character.");
            fullscreen.Remove(GameViewWindow.From(second));
            WaitForWindowPoll(() => !warning.IsVisible);
            check(!warning.IsVisible && host.ActiveGameViewName is null &&
                File.ReadAllBytes(GameViewLayouts.PathFor(profile)).SequenceEqual(beforeWarning),
                "The final mode change clears the warning without Apply or a preset edit, even with an unrelated fullscreen game open.");
            roster = [];
            fullscreen.Clear();
            windows = [first, second];

            static GameViewLayoutWindow.Display Display(string name, Rectangle bounds, string id, bool primary = false) =>
                new(name, bounds, bounds, primary, id);
            static C.Button Tile(GameViewLayoutWindow wizard, GameViewMonitor monitor) =>
                wizard.ScreenMap.Children.OfType<C.Button>().Single(tile => ((GameViewMonitor)tile.Tag).Device == monitor.Device);
            static string MapLabel(GameViewLayoutWindow wizard, GameViewMonitor monitor) =>
                System.Windows.Automation.AutomationProperties.GetName(Tile(wizard, monitor));
            static IEnumerable<W.DependencyObject> Descendants(W.DependencyObject parent)
            {
                for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
                {
                    var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                    yield return child;
                    foreach (var nested in Descendants(child)) yield return nested;
                }
            }
            static C.Border[] Seats(GameViewLayoutWindow wizard, GameViewMonitor monitor)
            {
                wizard.UpdateLayout();
                return Descendants(Tile(wizard, monitor)).OfType<C.Border>().Where(b => b.Tag is GameViewMapSlot).ToArray();
            }
            static bool OnlyMapStar(GameViewScreenMap map, C.Border expected)
            {
                var star = Descendants(expected).OfType<W.Shapes.Path>().SingleOrDefault();
                return star is not null && expected.Tag is GameViewMapSlot { SwapTarget: true } &&
                    Equals(star.Fill, expected.FindResource("SwapTargetStar")) &&
                    Equals(expected.Background, expected.FindResource("SwapTargetSurface")) &&
                    Descendants(map).OfType<W.Shapes.Path>().Count() == 1;
            }
            static bool OnlyStar(GameViewLayoutWindow wizard, C.Border expected) => OnlyMapStar(wizard.ScreenMap, expected);
            void CheckProjection(GameViewScreenMap map, GameViewMonitor monitor, GameViewKind kind, int count, string message,
                bool avoidTaskbar = false, GameViewEdge edge = GameViewEdge.Bottom, C.Canvas? detail = null)
            {
                var bounds = monitor.Bounds;
                var expected = GameViewGeometry.Slots(kind, avoidTaskbar ? monitor.WorkArea : bounds, count, edge);
                void CheckView(W.FrameworkElement frame, W.FrameworkElement[] seats)
                {
                    frame.UpdateLayout();
                    if (seats.Length != expected.Length || frame.ActualWidth <= 0 || frame.ActualHeight <= 0)
                        throw new InvalidOperationException($"{message} Frame {frame.RenderSize}, seats {seats.Length}/{count}.");
                    for (int i = 0; i < seats.Length; i++)
                    {
                        var slot = expected[i];
                        double xScale = frame.ActualWidth / bounds.Width, yScale = frame.ActualHeight / bounds.Height;
                        var projected = new W.Rect((slot.X - bounds.X) * xScale, (slot.Y - bounds.Y) * yScale,
                            slot.Width * xScale, slot.Height * yScale);
                        var rendered = new W.Rect(seats[i].TranslatePoint(new W.Point(), frame), seats[i].RenderSize);
                        // Read the rendered edges against the full monitor tile to catch extra padding and header strips.
                        const double tolerance = 1.05;
                        if (Math.Abs(projected.Left - rendered.Left) > tolerance || Math.Abs(projected.Right - rendered.Right) > tolerance ||
                            Math.Abs(projected.Top - rendered.Top) > tolerance || Math.Abs(projected.Bottom - rendered.Bottom) > tolerance)
                            throw new InvalidOperationException($"{message} Slot {i + 1}: expected {projected}, rendered {rendered} on {frame.GetType().Name}.");
                    }
                }
                map.UpdateLayout();
                var tile = map.Children.OfType<C.Button>().Single(b => ((GameViewMonitor)b.Tag).Device == monitor.Device);
                CheckView(tile, Descendants(tile).OfType<C.Border>().Where(b => b.Tag is GameViewMapSlot).ToArray());
                if (detail is not null) CheckView(detail, detail.Children.OfType<C.Button>().ToArray());
                check(true, message);
            }
            var displays = new[] {
                Display(@"\\.\DISPLAY1", new(0, 0, 1920, 1080), "panel-a", true),
                Display(@"\\.\DISPLAY2", new(640, -1080, 1920, 1080), "panel-b"),
                Display(@"\\.\DISPLAY4", new(-1080, -240, 1080, 1920), "panel-c")
            };
            var multi = new GameViewLayoutWindow(setup, profile, () => ["Paladin", "Cleric", "Bard"], () => [], host,
                () => windows, createNew: true, displayProvider: () => displays);
            try
            {
                multi.Show();
                multi.Width = multi.MinWidth;
                multi.Height = multi.MinHeight;
                multi.UpdateLayout();
                check(multi.DesignPage.ViewportHeight > 0 && multi.ScreenMap.ActualWidth > 80 &&
                    multi.DesignPage.TranslatePoint(new W.Point(0, multi.DesignPage.ActualHeight), multi).Y <=
                    multi.SaveLayoutButton.TranslatePoint(new W.Point(0, 0), multi).Y,
                    "At minimum window size the monitor map and save controls remain accessible.");
                var center = Tile(multi, displays[0]);
                var above = Tile(multi, displays[1]);
                var left = Tile(multi, displays[2]);
                check(multi.ScreenMap.Children.OfType<C.Button>().Count() == 3 &&
                    C.Canvas.GetLeft(left) < C.Canvas.GetLeft(center) && C.Canvas.GetLeft(above) > C.Canvas.GetLeft(center) &&
                    C.Canvas.GetTop(above) < C.Canvas.GetTop(left) && C.Canvas.GetTop(left) < C.Canvas.GetTop(center) &&
                    left.ActualHeight > left.ActualWidth && center.ActualWidth > center.ActualHeight,
                    "The monitor map uses Windows' horizontal and vertical offsets and preserves portrait orientation.");
                check(center.Focusable && left.Focusable && System.Windows.Automation.AutomationProperties.GetName(center).Contains("Unused"),
                    "The monitor tiles can receive keyboard focus and announce unused screens.");
                multi.LayoutName.Text = "Mixed monitors";
                Click(multi.SaveLayoutButton);
                check(multi.Feedback.IsVisible && !GameViewLayouts.Load(profile).Any(l => l.Name == "Mixed monitors"),
                    "Saving with every screen unused gives feedback and leaves the saved presets intact.");
                multi.Capacity.SelectedItem = 2;
                multi.SlotRows[1].Character.Text = "Paladin";
                Click(Tile(multi, displays[2]));
                check(Equals(multi.Capacity.SelectedItem, 0) && multi.EditingScreenTitle.Text.Contains("Screen 4"),
                    "Clicking an unused monitor opens that screen with no reserved slots.");
                multi.Capacity.SelectedItem = 3;
                Choose(multi.Arrangement, "Equal rows");
                Click((C.Button)multi.Diagram.Children[2]);
                multi.SlotRows[2].Character.Text = "Cleric";
                multi.SlotRows[0].WindowPicker.SelectedIndex = 1;
                multi.SwapOnClick.IsChecked = true;
                multi.MasterScreen.SelectedIndex = 1;
                multi.MasterSeat.SelectedItem = 3;
                check(MapLabel(multi, displays[2]).Contains("Swap target") && MapLabel(multi, displays[1]).Contains("Unused"),
                    "The map distinguishes the swap target from a display left unused.");
                var rows = Seats(multi, displays[2]);
                var columns = Seats(multi, displays[0]);
                check(rows.Length == 3 && C.Canvas.GetTop(rows[0]) < C.Canvas.GetTop(rows[1]) &&
                    C.Canvas.GetTop(rows[1]) < C.Canvas.GetTop(rows[2]) && C.Canvas.GetLeft(rows[0]) == C.Canvas.GetLeft(rows[2]) &&
                    rows.All(row => row.ActualHeight > 0 && row.ActualWidth > 0) &&
                    columns.Length == 2 && C.Canvas.GetLeft(columns[0]) < C.Canvas.GetLeft(columns[1]) &&
                    C.Canvas.GetTop(columns[0]) == C.Canvas.GetTop(columns[1]) && Seats(multi, displays[1]).Length == 0,
                    "The physical map draws horizontal and vertical seat arrangements inside their respective monitors and leaves the unused panel clear.");
                check(OnlyStar(multi, rows[2]) &&
                    System.Windows.Automation.AutomationProperties.GetName(rows[2]).Contains("Cleric") &&
                    System.Windows.Automation.AutomationProperties.GetName(rows[0]).Contains("Window") &&
                    System.Windows.Automation.AutomationProperties.GetName(columns[1]).Contains("Paladin"),
                    "Only the actual destination row has a yellow surface and star; configured characters and manually chosen windows are identified on the map.");
                CheckProjection(multi.ScreenMap, displays[2], GameViewKind.Rows, 3,
                    "Every portrait seat reaches the physical monitor edges at compact wizard size.");
                multi.DesignStep.IsChecked = true;
                multi.UpdateLayout();
                var detailTarget = (C.Button)multi.Diagram.Children[2];
                check(Equals(detailTarget.Background, detailTarget.FindResource("SwapTargetSurface")) &&
                    Descendants(detailTarget).OfType<W.Shapes.Path>().Count() == 1 && Descendants(multi.Diagram).OfType<W.Shapes.Path>().Count() == 1,
                    "The selected monitor's detailed arrangement marks the same target slot as the overview.");
                multi.AssignStep.IsChecked = true;
                Click(Tile(multi, displays[0]));
                check(multi.AssignStep.IsChecked == true && multi.SlotRows[1].Character.Text == "Paladin",
                    "Switching assignment screens keeps characters in their own numbered seats.");
                multi.Capacity.SelectedItem = 3;
                Click(multi.SaveLayoutButton);
                var mixed = GameViewLayouts.Load(profile).Single(l => l.Name == "Mixed monitors");
                check(mixed.ScreenPlans.Select(s => s.Monitor).SequenceEqual([displays[0].Device, displays[2].Device]) &&
                    mixed.ScreenPlans.Select(s => s.MonitorId).SequenceEqual(["panel-a", "panel-c"]) &&
                    mixed.ScreenPlans[1].Kind == GameViewKind.Rows && mixed.MasterIndex == 5 &&
                    mixed.Characters.SequenceEqual(["", "Paladin", "", "", "", "Cleric"]) && mixed.Windows![3] == GameViewWindow.From(windows[0]),
                    "The wizard saves nonconsecutive monitors, independent arrangements, exact assignments, and the chosen master.");
                Click(multi.NewLayout);
                SelectLayout(multi, "Mixed monitors");
                Click(Tile(multi, displays[2]));
                check(multi.MasterScreen.SelectedIndex == 1 && Equals(multi.MasterSeat.SelectedItem, 3) &&
                    multi.SlotRows[2].Character.Text == "Cleric" && multi.SlotRows[0].WindowPicker.SelectedIndex == 1,
                    "Reopening the mixed preset restores the master location and both kinds of assignment.");
                multi.Capacity.SelectedItem = 0;
                multi.UpdateLayout();
                check(multi.SwapOnClick.IsChecked == false,
                    "Setting the master screen to zero slots returns the layout to fixed slots.");
                check(!Descendants(multi.ScreenMap).OfType<W.Shapes.Path>().Any(), "Fixed mode clears the star from every screen.");
                multi.Capacity.SelectedItem = 3;
                check(multi.SlotRows[2].Character.Text == "Cleric", "Restoring slots before saving recovers that monitor's draft assignments.");
                Click(Tile(multi, displays[0]));
                multi.SlotRows[0].Character.Text = "Cleric";
                Click(multi.SaveLayoutButton);
                check(multi.Feedback.Text.Length > 0 && GameViewLayouts.Load(profile).Single(l => l.Name == "Mixed monitors").MasterIndex == 5,
                    "A duplicate assignment across screens cannot overwrite the saved layout.");
                SelectLayout(multi, "Grid");
                SelectLayout(multi, "Mixed monitors");
                Click(Tile(multi, displays[2]));
                multi.Width = 780;
                multi.Height = 820;
                multi.DesignStep.IsChecked = true;
                multi.UpdateLayout();
                string fixtureFolder = Path.GetDirectoryName(profile)!;
                GroupEditorTests.Screenshot((W.FrameworkElement)multi.Content, Path.Combine(fixtureFolder, "layout-wizard.png"));
                GroupEditorTests.Screenshot((W.FrameworkElement)multi.ScreenMap.Parent, Path.Combine(fixtureFolder, "monitor-map.png"));
                multi.DesignPage.ScrollToBottom();
                multi.UpdateLayout();
                check(new[] { multi.SwapOnClick, multi.OverlapTaskbar, multi.AlwaysOnTop, multi.MatchMasterResolution, multi.HideTitleBars }.All(toggle =>
                    toggle.IsVisible && toggle.ActualHeight > 0 && toggle.TranslatePoint(new W.Point(), multi.DesignPage).Y >= 0 &&
                    toggle.TranslatePoint(new W.Point(0, toggle.ActualHeight), multi.DesignPage).Y <= multi.DesignPage.ActualHeight),
                    "All switches are reachable together within the wizard's scrolled layout options.");
                GroupEditorTests.Screenshot((W.FrameworkElement)multi.Content, Path.Combine(fixtureFolder, "layout-wizard-switches.png"));
                Click(multi.NewLayout);
                multi.LayoutName.Text = "Secondary only";
                Click(Tile(multi, displays[1]));
                multi.Capacity.SelectedItem = 1;
                Click(multi.SaveLayoutButton);
                var only = GameViewLayouts.Load(profile).Single(l => l.Name == "Secondary only");
                check(only.ScreenPlans.Length == 1 && only.ScreenPlans[0].MonitorId == "panel-b" && only.Characters.SequenceEqual([""]),
                    "A single seat on a secondary monitor needs no slots or assignments on the primary.");
                var projector = Display(@"\\.\DISPLAY9", new(2560, -1080, 1920, 1080), "projector");
                displays = [.. displays, projector];
                multi.RefreshDisplays();
                check(multi.ScreenMap.Children.OfType<C.Button>().Count() == 4 && multi.EditingScreenTitle.Text.Contains("Screen 2") &&
                    Equals(multi.Capacity.SelectedItem, 1), "Connecting another monitor adds a tile and keeps the current screen selected.");
                Click(Tile(multi, projector));
                check(Equals(multi.Capacity.SelectedItem, 0), "A newly connected screen starts unused.");
                displays = displays[..^1];
                multi.RefreshDisplays();
                check(multi.ScreenMap.Children.OfType<C.Button>().Count() == 3 && multi.OfflineScreens.Items.Count == 0,
                    "Disconnecting an unused monitor removes its tile without creating a missing-screen assignment.");
                Click(Tile(multi, displays[1]));
                multi.Capacity.SelectedItem = 2;
                multi.SwapOnClick.IsChecked = true;
                multi.MasterScreen.SelectedIndex = 0;
                multi.MasterSeat.SelectedItem = 2;
                multi.LayoutName.Text = "Secondary swap override";
                Click(multi.SaveLayoutButton);
                var secondarySwap = GameViewLayouts.Load(profile).Single(l => l.Name == "Secondary swap override");
                check(secondarySwap.ScreenPlans.Single().MonitorId == "panel-b" && secondarySwap.MasterIndex == 1,
                    "A secondary-only layout can explicitly use its own second slot for swapping and leave the Windows main display unused.");
            }
            finally { multi.Close(); }
            displays = [displays[0], displays[1] with { Device = @"\\.\DISPLAY4" }, displays[2] with { Device = @"\\.\DISPLAY8" }];
            var renamed = new GameViewLayoutWindow(setup, profile, () => [], () => [], host, () => windows,
                initialLayout: "Mixed monitors", displayProvider: () => displays);
            try
            {
                renamed.Show();
                check(renamed.SwapOnClick.IsChecked == true, "Opening a saved swap layout keeps swap mode enabled.");
                Click(Tile(renamed, displays[2]));
                check(renamed.EditingScreenTitle.Text.Contains("Screen 8") && renamed.SlotRows[2].Character.Text == "Cleric" &&
                    renamed.MasterScreen.SelectedIndex == 1 && renamed.MasterSeat.SelectedIndex == 2,
                    "A renumbered monitor keeps its seats and master selection when the preset is opened.");
                var detached = displays[2];
                displays = [displays[0], displays[1] with { Device = detached.Device }];
                renamed.RefreshDisplays();
                check(renamed.SwapOnClick.IsChecked == true, "Disconnecting the swap display preserves its saved swap mode.");
                check(renamed.OfflineScreens.Items.Count == 1 && renamed.ScreenMap.Children.OfType<C.Button>().Count() == 2 &&
                    renamed.MasterScreen.SelectedItem.ToString()!.Contains("disconnected") && renamed.SlotRows[2].Character.Text == "Cleric",
                    "A different panel reusing the disconnected screen number cannot inherit its slots.");
                displays = [..displays, detached with { Device = @"\\.\DISPLAY7" }];
                renamed.RefreshDisplays();
                check(renamed.SwapOnClick.IsChecked == true && renamed.EditingScreenTitle.Text.Contains("Screen 7") && renamed.MasterSeat.SelectedIndex == 2,
                    "A returning monitor restores the correct screen selection while the wizard is open.");
                displays = [displays[0], displays[1]];
                renamed.RefreshDisplays();
                renamed.ReplacementScreen.SelectedItem = displays[1];
                Click(renamed.MoveToScreen);
                check(renamed.SwapOnClick.IsChecked == true, "Moving the slots to a replacement monitor preserves swap mode.");
                Click(renamed.SaveLayoutButton);
                var remapped = GameViewLayouts.Load(profile).Single(l => l.Name == "Mixed monitors");
                check(remapped.ScreenPlans.Select(s => s.MonitorId).SequenceEqual(["panel-a", "panel-b"]) &&
                    remapped.Characters.SequenceEqual(["", "Paladin", "", "", "", "Cleric"]) && remapped.MasterIndex == 5 &&
                    remapped.Windows![3] == GameViewWindow.From(windows[0]) && renamed.OfflineScreens.Items.Count == 0,
                    "Explicitly replacing a disconnected monitor preserves both screens' assignments and the master slot.");
            }
            finally { renamed.Close(); }

            var main = Display(@"\\.\DISPLAY2", new(0, 0, 1920, 1080), "windows-main", true);
            var side = Display(@"\\.\DISPLAY3", new(-1920, 0, 1920, 1080), "other-panel") with { WorkArea = new(-1840, 0, 1840, 1080) };
            var extra = Display(@"\\.\DISPLAY5", new(1920, 0, 1920, 1080), "spare");
            var pair = new[] { side, main };
            var routing = new GameViewLayoutWindow(setup, profile, () => [], () => [], host, () => [],
                createNew: true, displayProvider: () => pair);
            try
            {
                routing.Show();
                routing.LayoutName.Text = "Swap destination";
                Click(Tile(routing, side));
                routing.Capacity.SelectedItem = 1;
                routing.OverlapTaskbar.IsChecked = true;
                CheckProjection(routing.ScreenMap, side, GameViewKind.EqualSplit, 1,
                    "One slot reaches all four monitor edges in the monitor map and the detail preview.", detail: routing.Diagram);
                routing.OverlapTaskbar.IsChecked = false;
                CheckProjection(routing.ScreenMap, side, GameViewKind.EqualSplit, 1,
                    "Keeping the taskbar clear leaves only the left taskbar strip free on the negative-origin monitor.",
                    avoidTaskbar: true, detail: routing.Diagram);
                routing.OverlapTaskbar.IsChecked = true;
                routing.Capacity.SelectedItem = 2;
                Choose(routing.Arrangement, "50/50 · stacked");
                CheckProjection(routing.ScreenMap, side, GameViewKind.Rows, 2, "The stacked split fills the top and bottom halves.");
                Choose(routing.Arrangement, "Large + small corner");
                CheckProjection(routing.ScreenMap, side, GameViewKind.Corner, 2, "The corner layout preserves the native unused desktop space.");
                Choose(routing.Arrangement, "Main + preview strip");
                routing.Edge.SelectedItem = GameViewEdge.Right;
                CheckProjection(routing.ScreenMap, side, GameViewKind.MainStrip, 2,
                    "The right-hand preview strip leaves its unused lower portion free.", edge: GameViewEdge.Right);
                Choose(routing.Arrangement, "50/50 · side by side");
                CheckProjection(routing.ScreenMap, side, GameViewKind.EqualSplit, 2,
                    "The two side-by-side windows meet at the center and span the full height of the monitor.");
                var sideTile = Tile(routing, side);
                var leftEdgeOfMain = Tile(routing, main).TranslatePoint(new W.Point(), routing.ScreenMap).X;
                var rightEdgeOfSide = sideTile.TranslatePoint(new W.Point(sideTile.ActualWidth, 0), routing.ScreenMap).X;
                check(Math.Abs(leftEdgeOfMain - rightEdgeOfSide) <= 1.05, "Adjacent physical displays share an edge in the screen map.");
                routing.SwapOnClick.IsChecked = true;
                var sideSeats = Seats(routing, side);
                check(Equals(routing.MasterSeat.SelectedItem, 1) &&
                    !MapLabel(routing, main).Contains("Swap target") && OnlyStar(routing, sideSeats[0]) &&
                    System.Windows.Automation.AutomationProperties.GetName(sideSeats[0]).Contains("Unassigned"),
                    "When Windows main is unused, the sole configured monitor shows a yellow star on its first reserved slot even while it is empty.");
                routing.SlotRows[0].Character.Text = "Warrior";
                routing.SlotRows[1].Character.Text = "Bard";
                Click(Tile(routing, main));
                sideSeats = Seats(routing, side);
                check(OnlyStar(routing, sideSeats[0]) && MapLabel(routing, side).Contains("Warrior") && MapLabel(routing, side).Contains("Bard"),
                    "The two configured windows and their target remain visible while editing the unused screen.");
                var mainTile = Tile(routing, main);
                check(Descendants(mainTile).OfType<C.TextBlock>().Any(text => text.Text == "2") &&
                    !MapLabel(routing, main).Contains("Windows main", StringComparison.OrdinalIgnoreCase) &&
                    !routing.EditingScreenTitle.Text.Contains("Windows main", StringComparison.OrdinalIgnoreCase) &&
                    !Descendants(routing).OfType<C.TextBlock>().Any(text => text.IsVisible &&
                        text.Text.Contains("Windows main", StringComparison.OrdinalIgnoreCase)),
                    "The primary screen is identified by its number in the map and editor without a Windows main label.");
                GroupEditorTests.Screenshot((W.FrameworkElement)routing.ScreenMap.Parent,
                    Path.Combine(Path.GetDirectoryName(profile)!, "two-window-swap-map.png"));
                pair = [side, main, extra];
                routing.RefreshDisplays();
                Click(Tile(routing, extra));
                routing.Capacity.SelectedItem = 1;
                Click(routing.SaveLayoutButton);
                check(routing.MasterScreen.SelectedItem is null && routing.MasterSeat.SelectedItem is null &&
                    !Descendants(routing.ScreenMap).OfType<W.Shapes.Path>().Any() &&
                    !GameViewLayouts.Load(profile).Any(l => l.Name == "Swap destination"),
                    "With two eligible secondary monitors and no slots on Windows main, the map leaves the target open for an explicit choice.");
                Click(Tile(routing, main));
                routing.Capacity.SelectedItem = 1;
                Click(Tile(routing, side));
                check(MapLabel(routing, main).Contains("Swap target") &&
                    Equals(routing.MasterSeat.SelectedItem, 1) && OnlyStar(routing, Seats(routing, main)[0]) &&
                    routing.EditingScreenTitle.Text.Contains("Screen 3"),
                    "Screen 2 receives the automatic swap destination as soon as it has a seat, even while editing Screen 3.");

                pair = [side with { Primary = true }, main with { Primary = false }, extra];
                routing.RefreshDisplays();
                check(MapLabel(routing, side).Contains("Swap target") &&
                    !MapLabel(routing, main).Contains("Swap target"), "An automatic destination follows a change of Windows main display in the open wizard.");
                pair = [side, main, extra];
                routing.RefreshDisplays();
                routing.MasterScreen.SelectedItem = routing.MasterScreen.Items.Cast<object>()
                    .Single(item => item.ToString()!.StartsWith("Screen 3", StringComparison.Ordinal));
                routing.MasterSeat.SelectedItem = 2;
                Click(Tile(routing, main));
                routing.Capacity.SelectedItem = 3;
                check(MapLabel(routing, side).Contains("Swap target") && !MapLabel(routing, main).Contains("Swap target") &&
                    Equals(routing.MasterSeat.SelectedItem, 2) && OnlyStar(routing, Seats(routing, side)[1]),
                    "Choosing Screen 3 explicitly keeps its second seat starred while editing Screen 2.");
                routing.SwapOnClick.IsChecked = false;
                check(!Descendants(routing.ScreenMap).OfType<W.Shapes.Path>().Any(),
                    "Disabling swapping clears the active destination star.");
                Click(routing.SaveLayoutButton);
                Click(routing.NewLayout);
                pair = [main, extra, side with { Device = @"\\.\DISPLAY8" }];
                routing.RefreshDisplays();
                SelectLayout(routing, "Swap destination");
                check(routing.SwapOnClick.IsChecked == false && !Descendants(routing.ScreenMap).OfType<W.Shapes.Path>().Any(),
                    "Reopening the preset keeps swapping off after Windows renumbers the display.");
                routing.SwapOnClick.IsChecked = true;
                check(MapLabel(routing, pair[2]).Contains("Swap target") && Equals(routing.MasterSeat.SelectedItem, 2) &&
                    OnlyStar(routing, Seats(routing, pair[2])[1]), "Re-enabling swapping restores the explicit destination on the renumbered panel.");
            }
            finally { routing.Close(); }
            string oldProfile = Path.Combine(Path.GetDirectoryName(profile)!, "legacy-screen-settings.json");
            File.WriteAllText(GameViewLayouts.PathFor(oldProfile), """
                {"Version":2,"Layouts":[{"Name":"Old swap","Kind":"EqualSplit","Monitor":"Display 1","SwapOnClick":true,"Characters":["Warrior","Cleric"]}]}
                """);
            var legacy = new GameViewLayoutWindow(setup, oldProfile, () => [], () => [], host, () => [],
                initialLayout: "Old swap", displayProvider: () => [Display("Display 1", new(0, 0, 1600, 900), "legacy-panel", true)]);
            try
            {
                legacy.Show();
                check(legacy.SwapOnClick.IsChecked == true &&
                    legacy.OverlapTaskbar.IsChecked == true && legacy.AlwaysOnTop.IsChecked == false,
                    "Opening a legacy swapping preset restores its swap and taskbar settings.");
                legacy.AlwaysOnTop.IsChecked = true;
                Click(legacy.SaveLayoutButton);
                var savedLegacy = GameViewLayouts.Load(oldProfile).Single();
                check(savedLegacy.AlwaysOnTop && savedLegacy.MasterIndex == 0 && !savedLegacy.AvoidTaskbar &&
                    savedLegacy.Characters.SequenceEqual(["Warrior", "Cleric"]),
                    "Enabling always-on-top on a legacy layout preserves its destination, window preferences, and characters.");
            }
            finally { legacy.Close(); }
            setup.ShowHotKeys();
            OpenScreens();
            setup.ScreenLayoutPicker.SelectedItem = setup.ScreenLayoutPicker.Items.OfType<GameViewLayout>().Single(l => l.Name == "Mixed monitors");
            var seatSummary = new System.Windows.Documents.TextRange(setup.ScreenLayoutSlots.ContentStart, setup.ScreenLayoutSlots.ContentEnd).Text;
            check(setup.ScreenLayoutDescription.Text.Contains("2 screens") && setup.ScreenLayoutDescription.Text.Contains("6 slots") &&
                setup.ScreenSwappingSwitch.IsChecked == true && setup.ScreenLayoutSlots.Inlines.OfType<System.Windows.Documents.Bold>().Count() == 2 &&
                seatSummary.Contains("2   Paladin") && seatSummary.Contains("3   Cleric"),
                "The Screens page groups seats under each monitor and shows the saved swapping preference.");
        }
        finally
        {
            host.EndGameView();
            foreach (var setup in host.setups.Values.ToArray()) setup.CloseForExit();
            Current = null;
        }
    }
}
