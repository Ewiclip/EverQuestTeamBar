using W = System.Windows;
using C = System.Windows.Controls;
using M = System.Windows.Media;
using D = System.Windows.Threading;

namespace TeamBar;

internal partial class OverlayButtonEditor
{
    private static void TestIconGallery(FloatingTheme theme, StandaloneButton source, Action<bool, string> check, string output)
    {
        StandaloneButton? saved = null;
        var panel = (OverlayButtonEditor)CreateEditor(theme, source, false, 1, draft => saved = draft);
        // Only the first item tile contains opaque pixels. The other slots exercise
        // the same unavailable-art path as an empty cell or missing spell sheet.
        string fixture = Directory.CreateTempSubdirectory("teambar-gallery-").FullName;
        string ui = Directory.CreateDirectory(Path.Combine(fixture, "uifiles", "default")).FullName;
        File.WriteAllText(Path.Combine(ui, "EQUI_DragItems.xml"), """
            <XML><Ui2DAnimation item="A_DragItem"><Grid>true</Grid><Vertical>false</Vertical>
            <CellWidth>40</CellWidth><CellHeight>40</CellHeight><Frames><Texture>dragitem1.tga</Texture>
            <Location><X>0</X><Y>0</Y></Location><Size><CX>256</CX><CY>256</CY></Size></Frames></Ui2DAnimation></XML>
            """);
        var pixels = new byte[18 + 256 * 256 * 4];
        pixels[2] = 2; pixels[13] = 1; pixels[15] = 1; pixels[16] = 32; pixels[17] = 0x28;
        for (int y = 0; y < 40; y++)
            Array.Fill(pixels, (byte)255, 18 + y * 256 * 4, 40 * 4);
        File.WriteAllBytes(Path.Combine(ui, "dragitem1.tga"), pixels);
        var art = new EqIconArtwork(fixture);
        panel.currentArtwork = () => art;
        string[] keys = [.. Enumerable.Range(500, 60).Select(i => $"eq:item:{i}"), "eq:spell:500"];
        panel.readIconCatalog = _ => EqIconCatalog.FromNames(keys,
            [("eq:item:500", "Alderscale Shield"), ("eq:item:500", "Runed Buckler"), ("eq:spell:500", "Shield of Oaks")]);

        void Settle(Func<bool> complete)
        {
            long stop = Environment.TickCount64 + 20000;
            while (!complete())
            {
                if (Environment.TickCount64 >= stop) throw new TimeoutException("Icon picker did not finish: " + panel.IconStatus.Text);
                panel.Dispatcher.Invoke(() => { }, D.DispatcherPriority.ApplicationIdle);
                Thread.Sleep(5);
            }
        }
        C.Button Control(string label)
        {
            panel.PanelRoot.Measure(new W.Size(panel.Width, double.PositiveInfinity));
            panel.PanelRoot.Arrange(new W.Rect(0, 0, panel.Width, panel.PanelRoot.DesiredSize.Height));
            panel.PanelRoot.UpdateLayout();
            static IEnumerable<W.DependencyObject> Controls(W.DependencyObject parent)
            {
                for (int i = 0; i < M.VisualTreeHelper.GetChildrenCount(parent); i++)
                {
                    var child = M.VisualTreeHelper.GetChild(parent, i); yield return child;
                    foreach (var c in Controls(child)) yield return c;
                }
            }
            return Controls(panel.PanelRoot).OfType<C.Button>().First(b => W.Automation.AutomationProperties.GetName(b) == label);
        }
        void Click(string name) => Control(name).RaiseEvent(new W.RoutedEventArgs(C.Button.ClickEvent));
        try
        {
            Click("Choose button icon");
            Settle(() => panel.IconTiles.IsEnabled && panel.NextIconPage.IsEnabled);
            check(panel.IconTiles.Children.Count == IconsPerPage && !panel.PreviousIconPage.IsEnabled,
                "Browsing game icons renders only the first page of results.");
            Click("Next icon page");
            Settle(() => panel.IconTiles.IsEnabled && panel.PreviousIconPage.IsEnabled);
            check(!panel.NextIconPage.IsEnabled && panel.IconTiles.Children.Count == keys.Length - IconsPerPage,
                "The next icon page displays remaining results without materializing earlier tiles.");
            panel.IconSearch.Text = "500";
            Settle(() => panel.IconTiles.IsEnabled && panel.IconTiles.Children.Count == 2);
            check(panel.iconMatches!.Select(r => r.Kind).Distinct().Count() == 2 && !panel.PreviousIconPage.IsEnabled,
                "Searching by an icon ID finds both item and spell icons with that number and resets paging.");
            var missing = Control("Spell #500 · Shield of Oaks");
            check(!missing.IsEnabled && missing.ToolTip?.ToString()?.Contains("No artwork") == true,
                "A catalog entry with unavailable artwork is visibly disabled.");
            missing.RaiseEvent(new W.RoutedEventArgs(C.Button.ClickEvent));
            check(panel.IconBrowser.Visibility == W.Visibility.Visible && saved is null,
                "An unavailable result cannot change the draft even if a click is raised programmatically.");
            Click("Items"); panel.IconSearch.Text = "buckler";
            Settle(() => panel.IconTiles.IsEnabled && panel.IconTiles.Children.Count == 1 &&
                ((C.Button)panel.IconTiles.Children[0]).ToolTip?.ToString()?.Contains("Runed Buckler") == true);
            Click("Item #500 · Runed Buckler");
            check(panel.Body.Visibility == W.Visibility.Visible && saved is null,
                "Choosing a game icon returns to the original shortcut draft without saving it.");
            panel.currentArtwork = () => new EqIconArtwork(Path.Combine(fixture, "missing-install"));
            Click("Choose button icon"); Click("Close editor"); Click("Done");
            check(saved?.Icon == "eq:item:500" && saved.Rules.SequenceEqual(source.Rules) && source.Icon == "swords",
                "Closing the icon picker preserves its previous key and recipient shortcuts even when the artwork goes missing.");
            Click("Choose button icon"); Click("No icon"); Click("Done");
            check(saved?.Icon is null && saved is not null, "No icon explicitly clears the saved artwork.");
            string? previewFolder = Environment.GetEnvironmentVariable("TEAMBAR_TEST_ICON_PREVIEW_DIR");
            if (string.IsNullOrWhiteSpace(previewFolder)) return;
            var installed = new EqIconArtwork(previewFolder);
            panel.currentArtwork = () => installed;
            panel.readIconCatalog = a => EqIconCatalog.Load(a.GameDirectory, a.Keys());
            Click("Choose button icon"); Click("Spells"); panel.IconSearch.Text = "heal";
            Settle(() => panel.IconTiles.IsEnabled && panel.IconTiles.Children.Count > 0);
            // Force a full layout at the popup's actual width for the visual receipt.
            _ = Control("Spells");
            GroupEditorTests.Screenshot(panel.PanelRoot, output + ".eq-icon-picker.png");
        }
        finally { panel.Close(); Directory.Delete(fixture, recursive: true); }
    }
}
