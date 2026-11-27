using W = System.Windows;
using C = System.Windows.Controls;
using I = System.Windows.Input;
namespace TeamBar;

public partial class SetupWindow
{
    internal void TestDeletionUI(string folder, Action<bool, string> check)
    {
        StandaloneLibraryMode.IsChecked = true; LibrarySearch.Clear();
        var kept = standaloneDrafts.Single(b => b.Id == "healing");
        var removed = standaloneDrafts.Single(b => b.Id == "invisibility");
        ButtonList.SelectedItem = kept; Settle();
        var sourceBytes = File.ReadAllBytes(StandaloneButtons.PathFor(profilePath, removed.Id));
        DeleteRequest? asked = null;
        ConfirmDeletion = (_, request) => { asked = request; return false; };
        DeleteLibraryItem_Click(new C.Button { DataContext = removed }, new(C.Button.ClickEvent));
        check(asked?.Name == removed.Label && selected == kept && standaloneDrafts.Contains(removed)
            && sourceBytes.SequenceEqual(File.ReadAllBytes(StandaloneButtons.PathFor(profilePath, removed.Id))),
            "Canceling the inline X dialog preserves the clicked button, selected button, and saved file.");
        ConfirmDeletion = (_, _) => true;
        DeleteLibraryItem_Click(new C.Button { DataContext = removed }, new(C.Button.ClickEvent)); FlushAutoSave();
        check(standaloneDrafts.Contains(kept) && !standaloneDrafts.Contains(removed)
            && !File.Exists(StandaloneButtons.PathFor(profilePath, removed.Id))
            && Directory.GetFiles(Path.Combine(StandaloneButtons.DirectoryFor(profilePath), "removed"), removed.Id + "-*.json").Length == 1,
            "Confirmed deletion removes the clicked unselected button only and retains the existing recoverable archive.");
        ConfirmDeletion = DeleteConfirmation.Ask;
        var request = new DeleteRequest("Delete button?", kept.Label, "This removes the button from your library. Groups using it will no longer have this button available.");
        foreach (var theme in new[] { AppearanceTheme.Dark, AppearanceTheme.Light })
        {
            ThemePalette.Apply(Resources, theme);
            var dialog = new DeleteConfirmation(this, request);
            try { dialog.Show(); dialog.UpdateLayout(); dialog.Export(Path.Combine(folder, $"delete-confirmation-{theme.ToString().ToLowerInvariant()}.png")); }
            finally { dialog.Close(); }
        }
        var slide = new SlideConfirmation("delete"); int confirms = 0; long now = 0;
        slide.Clock = () => now; slide.Confirmed += () => confirms++;
        var fixture = new W.Window { Owner = this, Content = slide, Width = 450, Height = 100, ShowInTaskbar = false };
        fixture.Resources.MergedDictionaries.Add(Resources);
        try
        {
            fixture.Show(); fixture.UpdateLayout();
            check(slide.Travel > 250, "The confirmation handle has usable travel at the dialog's actual size.");
            int layoutPasses = 0;
            var invalidElements = new HashSet<string>();
            EventHandler layoutUpdated = (_, _) => layoutPasses++;
            var dragWatch = System.Diagnostics.Stopwatch.StartNew();
            slide.BeginDrag(); fixture.UpdateLayout();
            fixture.LayoutUpdated += layoutUpdated;
            for (int i = 0; i < 240; i++)
            {
                slide.DragBy(slide.Travel / 300);
                foreach (var element in SettingsDescendants(slide).OfType<W.UIElement>())
                    if (!element.IsMeasureValid || !element.IsArrangeValid) invalidElements.Add($"{element.GetType().Name}: measure={element.IsMeasureValid}, arrange={element.IsArrangeValid}");
                fixture.UpdateLayout();
            }
            slide.DragBy(slide.Travel); fixture.UpdateLayout();
            slide.Cancel(); fixture.UpdateLayout(); dragWatch.Stop();
            fixture.LayoutUpdated -= layoutUpdated;
            CollectorFormat.WriteNew(Path.Combine(folder, "slide-layout-cost.json"), new { moves = 240, layoutPasses, invalidElements, elapsedMs = dragWatch.Elapsed.TotalMilliseconds });
            check(invalidElements.Count == 0, "Pointer updates leave all slider elements measured and arranged.");
            double grab = 31;
            slide.BeginPointerDrag(grab); slide.MovePointer(grab + slide.Travel * .4);
            slide.MovePointer(grab + slide.Travel * .4);
            check(Math.Abs(slide.Progress - .4) < .001, "Repeated pointer positions do not accumulate movement before the next frame.");
            slide.MovePointer(grab + slide.Travel * .75); fixture.UpdateLayout();
            var handlePoint = slide.Handle.TranslatePoint(new(0, 0), slide);
            check(Math.Abs(handlePoint.X - (4 + slide.Travel * .75)) < .1,
                "The rendered handle follows the pointer one-to-one in the stationary track's coordinates.");
            slide.MovePointer(grab + slide.Travel + 50); slide.MovePointer(grab + slide.Travel * .6);
            check(Math.Abs(slide.Progress - .6) < .001, "Overshooting then moving back tracks the actual pointer without drift.");
            slide.Cancel(); slide.MovePointer(grab + slide.Travel); slide.EndDrag(false);
            check(confirms == 0, "Pointer events after cancellation cannot re-arm deletion.");
            slide.BeginDrag(); slide.DragBy(slide.Travel * .7); slide.EndDrag(false);
            check(confirms == 0 && slide.Progress == 0, "A partial slide resets without confirming.");
            slide.BeginDrag(); slide.DragBy(slide.Travel);
            check(confirms == 0 && slide.Progress == 1, "Reaching the end arms confirmation but does not delete before release.");
            slide.EndDrag(true); check(confirms == 0 && slide.Progress == 0, "A canceled full drag cannot confirm deletion.");
            slide.BeginDrag(); slide.DragBy(slide.Travel); slide.Cancel(); slide.EndDrag(false);
            check(confirms == 0, "Losing the dialog's active state invalidates a previously armed drag.");
            slide.BeginDrag(); slide.DragBy(slide.Travel); slide.DragBy(-slide.Travel * .2); slide.EndDrag(false);
            check(confirms == 0, "Moving back before release cancels a full slide.");
            slide.BeginDrag(); slide.DragBy(slide.Travel); slide.EndDrag(false); slide.EndDrag(false);
            check(confirms == 1, "One complete drag and release confirms once, without replay.");
            slide.BeginKeyboard(); now += 500; slide.AdvanceKeyboard(); slide.EndKeyboard();
            check(confirms == 1 && slide.Progress == 0, "A short Right Arrow press does not confirm.");
            slide.BeginKeyboard(); now += 1200; slide.AdvanceKeyboard();
            check(confirms == 1, "Keyboard confirmation also waits for release after a deliberate hold.");
            slide.EndKeyboard(); check(confirms == 2, "Holding and releasing Right Arrow provides keyboard access to confirmation.");
            slide.BeginKeyboard(); now += 1200; slide.AdvanceKeyboard();
            slide.Handle.RaiseEvent(new I.KeyEventArgs(I.Keyboard.PrimaryDevice, W.PresentationSource.FromVisual(slide), 0, I.Key.Enter) { RoutedEvent = I.Keyboard.PreviewKeyDownEvent });
            slide.EndKeyboard(); check(confirms == 2 && slide.Progress == 0, "Enter cancels progress and never serves as a deletion shortcut.");
        }
        finally { fixture.Close(); }
        var confirm = new DeleteConfirmation(this, request);
        confirm.Loaded += (_, _) => confirm.Dispatcher.BeginInvoke(() =>
        { confirm.Slide.BeginDrag(); confirm.Slide.DragBy(confirm.Slide.Travel); confirm.Slide.EndDrag(false); }, W.Threading.DispatcherPriority.ContextIdle);
        check(confirm.ShowDialog() == true, "The real modal returns success only after completing the slide.");
        var cancel = new DeleteConfirmation(this, request);
        cancel.Loaded += (_, _) => cancel.Dispatcher.BeginInvoke(() => cancel.CancelButton.RaiseEvent(new W.RoutedEventArgs(C.Button.ClickEvent)), W.Threading.DispatcherPriority.ContextIdle);
        check(cancel.ShowDialog() == false, "The real modal's Cancel action returns without authorizing deletion.");

        BarLibraryMode.IsChecked = true; LibrarySearch.Clear(); Settle();
        var group = selectedGroup!; int savedCount = StandaloneButtons.Load(profilePath).Length;
        static IEnumerable<W.DependencyObject> Children(W.DependencyObject parent)
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i); yield return child;
                foreach (var descendant in Children(child)) yield return descendant;
            }
        }
        var referenceX = Children(groupColumnsEditor!).OfType<C.Button>().First(b => W.Automation.AutomationProperties.GetName(b).StartsWith("Remove "));
        int memberCount = group.Buttons.Length;
        groupColumnsEditor!.ConfirmRemoval = (_, _) => false; referenceX.RaiseEvent(new W.RoutedEventArgs(C.Button.ClickEvent));
        check(selectedGroup!.Buttons.Length == memberCount, "Canceling a group-member removal preserves its reference.");
        groupColumnsEditor.ConfirmRemoval = (_, _) => true; referenceX.RaiseEvent(new W.RoutedEventArgs(C.Button.ClickEvent)); FlushAutoSave();
        check(selectedGroup.Buttons.Length == memberCount - 1 && StandaloneButtons.Load(profilePath).Length == savedCount,
            "Confirming a group-member removal changes only that group, preserving library buttons.");
        ConfirmDeletion = (_, _) => false; DeleteLibraryItem_Click(new C.Button { DataContext = selectedGroup }, new(C.Button.ClickEvent));
        check(groupDrafts.Count == 1, "Canceling group deletion preserves its layout.");
        ConfirmDeletion = (_, _) => true; DeleteLibraryItem_Click(new C.Button { DataContext = selectedGroup }, new(C.Button.ClickEvent)); FlushAutoSave();
        check(groupDrafts.Count == 0 && StandaloneButtons.Load(profilePath).Length == savedCount, "Confirmed group deletion preserves every library button.");
        ConfirmDeletion = DeleteConfirmation.Ask;
        StandaloneLibraryMode.IsChecked = true; ThemePalette.Apply(Resources, settings.Theme); Settle();
    }
}
