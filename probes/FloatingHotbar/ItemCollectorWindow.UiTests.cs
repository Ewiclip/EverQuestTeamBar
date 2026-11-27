using W = System.Windows;
using C = System.Windows.Controls;
using D = System.Windows.Documents;
using M = System.Windows.Media;
namespace TeamBar;

public partial class ItemCollectorWindow
{
    // Exercise compiled templates and deferred WPF layout, not just decoded records.
    internal static void RunUiTests(string output)
    {
        var app = new W.Application();
        var window = new ItemCollectorWindow(true);
        var passed = new List<string>(); Exception? failure = null;
        var at = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        var details = new CandidateDetails("Fixture lore", 10, 1, 65535, 65535, 0, 0, true, false, true, false, false, 0, 1, 2, 3, 4, 0, 0, 0, 20);
        var item = new ItemData(120780, "Fixture linked item", details);
        var capture = new InspectionCapture(1, Guid.NewGuid(), at, "Live", Identity.BuildHash, InspectionDecoder.Decoder,
            "item-display", CollectorFormat.DetailStatus, "Fixture", "Cleric", [], "Synthetic UI fixture");
        void Check(bool condition, string name)
        { if (!condition) throw new InvalidOperationException(name); passed.Add(name); }
        async Task Layout()
        {
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
            window.UpdateLayout();
        }
        static IEnumerable<string> Texts(W.DependencyObject root)
        {
            if (root is C.TextBlock text) yield return new D.TextRange(text.ContentStart, text.ContentEnd).Text;
            for (int i = 0; i < M.VisualTreeHelper.GetChildrenCount(root); i++)
                foreach (string value in Texts(M.VisualTreeHelper.GetChild(root, i))) yield return value;
        }
        window.ContentRendered += async (_, _) =>
        {
            try
            {
                foreach (var theme in new[] { AppearanceTheme.Light, AppearanceTheme.Dark })
                {
                    ThemePalette.Apply(window.Resources, theme);
                    await window.InitializeContributions();
                    Check(window.contributionClient is null && window.CollectorBrand.Text.Contains("PREVIEW")
                        && !window.RetryContributionsControl.IsEnabled,
                        $"{theme}: saved-snapshot review never constructs the uploader or reads its settings");
                    var contributionPreview = new ContributionSettingsWindow(window, null, true);
                    try
                    {
                        contributionPreview.Show(); await Layout(); contributionPreview.UpdateLayout();
                        Check(contributionPreview.PreviewNotice.IsVisible && !contributionPreview.SaveControl.IsEnabled
                            && !contributionPreview.ClearControl.IsEnabled && !contributionPreview.RetryControl.IsEnabled
                            && !contributionPreview.KeyInput.IsEnabled && contributionPreview.KeyInput.Password.Length == 0,
                            $"{theme}: contribution preview renders disabled credential and upload controls");
                        contributionPreview.DestinationChoice.SelectedValue = "eqtradeskills";
                        Check(contributionPreview.UploadState.Text.Contains("EQTradeskills") && !contributionPreview.SaveControl.IsEnabled
                            && !contributionPreview.KeyInput.IsEnabled && contributionPreview.KeyInput.Password.Length == 0,
                            $"{theme}: the second destination preview remains credential-free and cannot upload");
                        contributionPreview.DestinationChoice.SelectedValue = "dkp";
                        contributionPreview.Save_Click(contributionPreview.SaveControl, new W.RoutedEventArgs());
                        contributionPreview.Clear_Click(contributionPreview.ClearControl, new W.RoutedEventArgs());
                        contributionPreview.Retry_Click(contributionPreview.RetryControl, new W.RoutedEventArgs());
                        Check(window.contributionClient is null && contributionPreview.KeyInput.Password.Length == 0,
                            $"{theme}: preview handlers cannot save settings, protect credentials or upload");
                        Check(contributionPreview.RetryControl.TranslatePoint(new W.Point(0, contributionPreview.RetryControl.ActualHeight), contributionPreview).Y
                            < contributionPreview.SaveControl.TranslatePoint(new W.Point(0, 0), contributionPreview).Y,
                            $"{theme}: default contribution dialog keeps upload status and actions in view");
                        var content = (W.FrameworkElement)contributionPreview.Content;
                        var bitmap = new M.Imaging.RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight,
                            96, 96, M.PixelFormats.Pbgra32);
                        bitmap.Render(content);
                        var encoder = new M.Imaging.PngBitmapEncoder();
                        encoder.Frames.Add(M.Imaging.BitmapFrame.Create(bitmap));
                        string screenshot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, $"contributions-{theme.ToString().ToLowerInvariant()}.png");
                        using var image = new FileStream(screenshot, FileMode.Create);
                        encoder.Save(image);
                    }
                    finally { contributionPreview.Close(); }
                    window.history = new(); window.InspectionSearch.Text = "";
                    window.DataTabs.SelectedItem = window.InspectedTab;
                    window.UpdateSummary();
                    Check(window.SelectedSnapshot() is null && !window.ExportControl.IsEnabled,
                        $"{theme}: empty history has no fabricated export source even when the prior capture remains in the view");
                    window.AcceptInspection(capture); await Layout();
                    Check(window.InspectionList.Items.Count == 0, $"{theme}: empty inspected list renders");

                    window.AcceptInspection(capture with { Items = [new(0, item)] }); await Layout();
                    Check(Texts(window.InspectionList).Any(t => t.Contains("Item 120780") && t.Contains(at.ToLocalTime().ToString("HH:mm:ss")))
                        && window.InspectionTitle.Text == item.Name, $"{theme}: new linked item renders its read-only ID and last-seen text");

                    var later = capture with { ObservedAtUtc = at.AddSeconds(2), Items = [new(0, item), new(1, item with { ItemId = 175966, Name = "Second linked item" })] };
                    window.AcceptInspection(later); await Layout();
                    Check(window.InspectionList.Items.Count == 2 && window.InspectionTitle.Text == item.Name
                        && Texts(window.InspectionList).Any(t => t.Contains(later.ObservedAtUtc.ToLocalTime().ToString("HH:mm:ss"))),
                        $"{theme}: refresh preserves selection and updates visible timestamps");

                    window.InspectionSearch.Text = "no match"; await Layout();
                    Check(window.InspectionList.Items.Count == 0, $"{theme}: filtering to no results renders");
                    window.InspectionSearch.Text = "175966"; await Layout();
                    Check(window.InspectionList.Items.Count == 1 && window.InspectionTitle.Text == "Second linked item",
                        $"{theme}: filtering restores a populated item template");

                    var recipeSnapshot = new RecipeSnapshot(1, at, "Fixture", Identity.BuildHash, "recipe-window", "Fixture container", 10, 42,
                        [new(42, "Fixture recipe", 50, 1, 10, 0, false, [])], "Synthetic UI fixture");
                    window.AcceptRecipes(recipeSnapshot);
                    window.DataTabs.SelectedItem = window.RecipesTab; await Layout();
                    Check(window.IngredientList.Items.Count == 0 && window.IngredientState.Text.Contains("Summary only"),
                        $"{theme}: open-container recipe summary does not invent ingredients");
                    var loadedRecipe = recipeSnapshot.Recipes[0] with
                    { DetailsLoaded = true, IngredientSlots = [new(0, 120780, 10, "Fixture ingredient")] };
                    window.AcceptRecipes(recipeSnapshot with { ObservedAtUtc = at.AddSeconds(2), Recipes = [loadedRecipe] });
                    await Layout();
                    Check(Texts(window.IngredientList).Any(t => t.Contains("Item 120780") && t.Contains("Slot 0") && t.Contains("Icon 10")),
                        $"{theme}: next snapshot updates visible ingredients while the same container stays open");
                    window.AcceptRecipes(recipeSnapshot with { ObservedAtUtc = at.AddSeconds(4), Recipes = [loadedRecipe with
                        { IngredientSlots = [new(1, 175966, 12, "Updated ingredient")] }] });
                    await Layout();
                    Check(window.RecipeTitle.Text == "Fixture recipe" && window.IngredientList.Items.Count == 1
                        && Texts(window.IngredientList).Any(t => t.Contains("Item 175966"))
                        && !Texts(window.IngredientList).Any(t => t.Contains("Item 120780")),
                        $"{theme}: repeated open-container snapshot replaces stale ingredient details");
                    var currentLiveRecipe = LiveRecipeDecoderTests.FixtureCapture(true);
                    window.AcceptRecipes(currentLiveRecipe); window.UpdateSummary(); await Layout();
                    Check(window.RecipeTitle.Text == "Barbequed Minotaur Meat" && window.IngredientList.Items.Count == 2
                        && window.SourceState.Text.Contains(AdvancedLootMemoryLayout.NovemberBuildHash[..12])
                        && window.SelectedSnapshot() is RecipeSnapshot { Decoder: LiveRecipeDecoder.Decoder, IsOpen: true },
                        $"{theme}: current Live recipe observation renders selected ingredients and exports the exact decoder source");
                    window.AcceptRecipes(currentLiveRecipe with { Recipes = [], SelectedRecipeId = null }); window.UpdateSummary(); await Layout();
                    Check(window.RecipeList.Items.Count == 0 && window.IngredientList.Items.Count == 0 && window.ComparisonHint.Text.Contains("No recipes loaded"),
                        $"{theme}: an empty search clears stale recipes and explains how to load results");
                    window.AcceptRecipes(currentLiveRecipe with { IsOpen = false, Recipes = [], SelectedRecipeId = null, ContainerName = null, ContainerItemId = null });
                    window.UpdateSummary(); await Layout();
                    Check(window.RecipeList.Items.Count == 0 && window.ComparisonHint.Text.Contains("Open a tradeskill container"),
                        $"{theme}: closing the tradeskill window clears cached recipes and gives the next step");
                    window.DataTabs.SelectedItem = window.InspectedTab; await Layout();
                    Check(window.InspectionTitle.Text == "Second linked item", $"{theme}: returning to inspected items renders retained rows");

                    window.AcceptGatheringSnapshot(new(1, "gathering-session", "Live", Identity.BuildHash, "synthetic-gathering",
                        [new("foraged", "Pod of Water", new(123, "vortex", "The Vortex"), at), new("fished", "Fish Scales", new(123, "vortex", "The Vortex"), at)]));
                    window.DataTabs.SelectedItem = window.GatheringTab; await Layout();
                    Check(window.GatheringList.Items.Count == 2 && window.SelectedSnapshot() is GatheringHistorySnapshot && window.contributionClient is null,
                        $"{theme}: gathering history displays method and zone, exports a reviewable snapshot, and never starts uploading in preview");
                    GroupEditorTests.Screenshot(window.CollectorRoot, Path.Combine(Path.GetDirectoryName(output)!, "gathering-" + theme.ToString().ToLowerInvariant() + ".png"));
                    var target = new TargetCapture(1, Guid.NewGuid(), at, "Live", Identity.BuildHash, "live-selected-target-1",
                        "selected-target", 1, 1, "Fixture", "Cleric", new(151, "bazaar", "The Bazaar"),
                        new(11613, "Torm the Golden", 1, 1, "NPC"), "Synthetic UI fixture");
                    window.DataTabs.SelectedItem = window.TargetTab; window.AcceptTarget(target); await Layout();
                    Check(window.TargetTitle.Text == "Torm the Golden" && window.TargetType.Text == "NPC"
                        && window.TargetLevel.Text == "1" && window.TargetZone.Text == "The Bazaar" && window.ExportControl.IsEnabled,
                        $"{theme}: target name, type, level, zone and export render");
                    var effects = new TargetBuffCapture(1, Guid.NewGuid(), at, "Live", Identity.BuildHash, "fixture", "target-buffs",
                        1, 1, "Fixture", "Cleric", target.Zone, target.Target, "observed-target-window",
                        [new(0, 68116, "Warder's Alliance Rk. II", "fIXTURE", 10033000, 104),
                         new(1, 71724, "Other character's spell", "Xazaa", 10000, 104),
                         new(2, 71724, "Unknown caster's spell", null, 10000, 104)], null, null, "Synthetic UI fixture");
                    var withEffects = target with { Buffs = effects };
                    await window.ContributeItems(null, capture with { Items = [new(0, item)] }, window.generation, recipeSnapshot);
                    await window.ContributeSpells(withEffects, window.generation);
                    Check(window.contributionClient is null && !window.contributionItemsBusy && !window.contributionSpellsBusy,
                        $"{theme}: fixture captures cannot queue items or recipes or read installed spell definitions for upload");
                    window.AcceptTarget(withEffects); await Layout();
                    Check(window.mySpells.Count == 1 && window.mySpells[0].Name == "Warder's Alliance Rk. II"
                        && window.mySpells[0].Remaining == "2h 47m 13s" && Texts(window.MySpellList).Contains("2h 47m 13s"),
                        $"{theme}: own spell name and timer render; other and unknown casters stay hidden");
                    var retained = window.mySpells[0];
                    window.AcceptTarget(withEffects with { Buffs = effects with { Effects = [effects.Effects[0] with { RemainingMilliseconds = 10032000 }] } });
                    await Layout();
                    Check(ReferenceEquals(retained, window.mySpells[0]) && retained.Remaining == "2h 47m 12s"
                        && Texts(window.MySpellList).Contains("2h 47m 12s"), $"{theme}: game timer updates the existing visible row");
                    window.AcceptTarget(withEffects with { Buffs = effects with { Effects = [effects.Effects[0] with { RemainingMilliseconds = -1 }] } });
                    Check(retained.Remaining == "No timer", $"{theme}: absent duration is not presented as an expiry");
                    Check(MyTargetSpells.Export(withEffects).Buffs?.Effects.Length == 1,
                        $"{theme}: UI export contains only the observing character's spells");
                    foreach (var mismatched in new[] { effects with { CharacterName = "Other" }, effects with { ProcessId = 2 },
                        effects with { Target = effects.Target! with { SpawnId = 999 } }, effects with { Status = "waiting-for-target-data" } })
                    {
                        window.AcceptTarget(withEffects with { Buffs = mismatched });
                        Check(window.mySpells.Count == 0, $"{theme}: unavailable or mismatched spell source is hidden ({mismatched.CharacterName}/{mismatched.ProcessId}/{mismatched.Target?.SpawnId}/{mismatched.Status})");
                    }
                    window.AcceptTarget(withEffects with { BuffsError = "Unavailable" });
                    Check(window.mySpells.Count == 0 && window.MySpellEmpty.Text.Contains("unavailable"),
                        $"{theme}: failed spell reading clears old spells and explains the empty list");
                    window.AcceptTarget(target with { Target = null }); await Layout();
                    Check(window.TargetTitle.Text == "No target selected" && window.TargetLevel.Text == "—",
                        $"{theme}: clearing target removes prior NPC details");
                    window.AcceptTarget(target); window.targetError = "Synthetic unavailable record";
                    window.ShowTarget(); window.UpdateSummary(); await Layout();
                    Check(window.TargetTitle.Text == "Target unavailable" && window.TargetType.Text == "—" && !window.ExportControl.IsEnabled,
                        $"{theme}: failed reading cannot masquerade as a current target");
                    await TestMerchants(window, Check, theme.ToString());
                }

                TestCollectorBuildSelection(window, Check);
                TestCollectorDiscoveryWarnings(window, Check);
                await ContributionSettingsWindow.TestDestinations(window, Check,
                    Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "contribution-settings-fixture-" + Guid.NewGuid().ToString("N")));

                var icon = window.spellIcons.Get(104);
                Check(icon is { PixelWidth: 40, PixelHeight: 40, IsFrozen: true }, "installed EQ spell artwork loads as a frozen 40-pixel icon");
                byte[] tga = new byte[18 + 2 * 2 * 3]; tga[2] = 2; tga[12] = 2; tga[14] = 2; tga[16] = 24;
                new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }.CopyTo(tga, 18);
                var decoded = SpellIcons.DecodeTga(tga); byte[] pixels = new byte[16]; decoded.CopyPixels(pixels, 8, 0);
                Check(pixels.SequenceEqual(new byte[] { 7, 8, 9, 255, 10, 11, 12, 255, 1, 2, 3, 255, 4, 5, 6, 255 }),
                    "bottom-origin RGB artwork is converted with correct orientation and alpha");
                tga[17] = 0x30; decoded = SpellIcons.DecodeTga(tga); decoded.CopyPixels(pixels, 8, 0);
                Check(pixels.SequenceEqual(new byte[] { 4, 5, 6, 255, 1, 2, 3, 255, 10, 11, 12, 255, 7, 8, 9, 255 }),
                    "top-right-origin artwork is oriented correctly");
                bool truncatedRejected = false;
                try { SpellIcons.DecodeTga(tga[..^1]); } catch (InvalidDataException) { truncatedRejected = true; }
                Check(truncatedRejected, "truncated artwork is rejected");

                // Exercise the capture-to-queue boundary with controlled tasks.
                // No uploader, credential protection, settings or network exists.
                int captureGeneration = 1, queuedDefinitions = 0, exportedDefinitions = 0;
                var exportCompletion = new TaskCompletionSource<ContributionRecord[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                Task FakeEnqueue(ContributionRecord[] records, CancellationToken cancel)
                { queuedDefinitions++; return Task.CompletedTask; }
                var deferredContribution = QueueCurrentContributions(() => captureGeneration == 1,
                    _ => { exportedDefinitions++; return exportCompletion.Task; }, FakeEnqueue, CancellationToken.None);
                captureGeneration++;
                exportCompletion.SetResult([]);
                Check(!await deferredContribution && queuedDefinitions == 0 && exportedDefinitions == 1,
                    "character or settings change discards an already exporting contribution before enqueue");
                Check(!await QueueCurrentContributions(() => false,
                    _ => { exportedDefinitions++; return Task.FromResult(Array.Empty<ContributionRecord>()); }, FakeEnqueue, CancellationToken.None)
                    && exportedDefinitions == 1,
                    "a stale capture never starts contribution export");
                using (var contributionCancel = new CancellationTokenSource())
                {
                    exportCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    deferredContribution = QueueCurrentContributions(() => true, _ => exportCompletion.Task, FakeEnqueue, contributionCancel.Token);
                    contributionCancel.Cancel(); exportCompletion.SetResult([]);
                    bool cancelled = false;
                    try { await deferredContribution; } catch (OperationCanceledException) { cancelled = true; }
                    Check(cancelled && queuedDefinitions == 0, "closing cancels contribution work before it can enqueue");
                }
                Check(await QueueCurrentContributions(() => true,
                    _ => Task.FromResult(Array.Empty<ContributionRecord>()), FakeEnqueue, CancellationToken.None)
                    && queuedDefinitions == 1, "only a current capture reaches contribution enqueue");
                var enqueueCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                deferredContribution = QueueCurrentContributions(() => true,
                    _ => Task.FromResult(Array.Empty<ContributionRecord>()), (_, _) => enqueueCompletion.Task, CancellationToken.None);
                Check(!deferredContribution.IsCompleted, "a valid contribution remains active until local enqueue completes");
                enqueueCompletion.SetResult();
                Check(await deferredContribution, "local enqueue releases the capture cycle without waiting for HTTP");
                Check(SkippedDefinitionsMessage(0, 0) == "" && SkippedDefinitionsMessage(2, 3).Contains("2 item(s), 3 spell(s)"),
                    "unsupported observation counts have a visible warning without exposing captured details");

                var client = new DetectedClient(1, 1, 1, 0, new("Fixture", 2, 130));
                var poll = new TargetCapture(1, Guid.NewGuid(), at, "Live", Identity.BuildHash, "live-selected-target-1",
                    "selected-target", 1, 1, "Fixture", "Cleric", new(151, "bazaar", "The Bazaar"),
                    new(1, "Polling fixture", 10, 1, "NPC"), "Synthetic polling fixture");
                var completion = new TaskCompletionSource<TargetCapture>(TaskCreationOptions.RunContinuationsAsynchronously);
                int started = 0;
                window.ResetTarget();
                var inFlight = window.ReadTarget(client, () => { started++; return completion.Task; });
                await window.ReadTarget(client, () => { started++; return Task.FromResult(poll); });
                Check(started == 1, "slow target reads do not overlap or queue another read");
                window.Pause_Click(window.PauseControl, new W.RoutedEventArgs());
                completion.SetResult(poll); await inFlight;
                await window.ReadTarget(client, () => { started++; return Task.FromResult(poll); });
                Check(window.targetCapture is null && started == 1, "pause discards the in-flight reading and prevents another read");
                window.Pause_Click(window.PauseControl, new W.RoutedEventArgs());
                await window.ReadTarget(client, () => Task.FromResult(poll));
                Check(window.targetCapture == poll && !window.targetBusy, "resume accepts the next target reading");

                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                inFlight = window.ReadTarget(client, () => completion.Task);
                window.Client_Changed(window.ClientChoice, new C.SelectionChangedEventArgs(C.Primitives.Selector.SelectionChangedEvent, Array.Empty<object>(), Array.Empty<object>()));
                completion.SetResult(poll); await inFlight;
                Check(window.targetCapture is null, "character switch discards a read from the previous character");
                await window.ReadTarget(client, () => Task.FromResult(poll with { CharacterName = "Different" }));
                Check(window.targetCapture is null && window.targetError?.Contains("Character or persona changed") == true,
                    "identity mismatch rejects the polled target");
                window.targetTimer.Start();
                window.Close();
                await window.ReadTarget(client, () => { started++; return Task.FromResult(poll); });
                Check(window.closed && !window.targetTimer.IsEnabled && !window.timer.IsEnabled && started == 1,
                    "closing stops timers and prevents further target reads");
            }
            catch (Exception error) { failure = error; }
            finally { window.Close(); }
        };
        try { app.Run(window); }
        catch (Exception error) { failure ??= error; window.Close(); }
        CollectorFormat.WriteNew(output, new { passed = failure is null, tests = passed, error = failure?.ToString(), gameInputSent = false, processMemoryRead = false });
        if (failure is not null) throw new InvalidOperationException("Collector UI regression test failed.", failure);
    }
}
