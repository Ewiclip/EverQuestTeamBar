using System.Runtime.CompilerServices;
using W = System.Windows;
using C = System.Windows.Controls;
using M = System.Windows.Media;
using B = System.Windows.Media.Imaging;

namespace TeamBar;

internal partial class OverlayButtonEditor
{
    private const int IconsPerPage = 32;
    private static readonly ConditionalWeakTable<EqIconArtwork, Lazy<EqIconCatalog>> Catalogs = new();
    private Func<EqIconArtwork> currentArtwork = () => EqIconArtwork.Shared;
    private Func<EqIconArtwork, EqIconCatalog> readIconCatalog = art =>
        Catalogs.GetValue(art, a => new(() => EqIconCatalog.Load(a.GameDirectory, a.Keys()))).Value;
    private Action<string?>? chooseIcon;
    private C.Button? iconOpener;
    private string? currentIcon, iconKind;
    private int iconPage;
    private IReadOnlyList<EqIconSearchResult>? iconMatches;
    private CancellationTokenSource? iconWork;

    private W.FrameworkElement SavedIcon(string? key, double size, string fallback = "")
    {
        if (key?.StartsWith("eq:", StringComparison.Ordinal) == true && currentArtwork().Get(key) is { } image)
            return IconImage(image, size);
        string glyph = OverlayButtonIcons.Glyph(key);
        return IconLabel(glyph.Length > 0 ? glyph : fallback);
    }

    private static C.Image IconImage(B.BitmapSource image, double size)
    {
        var result = new C.Image { Source = image, Width = size, Height = size, Stretch = M.Stretch.Uniform,
            VerticalAlignment = W.VerticalAlignment.Center, HorizontalAlignment = W.HorizontalAlignment.Center };
        M.RenderOptions.SetBitmapScalingMode(result, M.BitmapScalingMode.HighQuality);
        return result;
    }

    private void OpenIconBrowser(C.Button opener, string? icon, Action<string?> select)
    {
        StopRecording();
        iconOpener = opener;
        currentIcon = icon;
        chooseIcon = select;
        if (IconCategories.Children.Count == 0)
        {
            foreach (var (kind, label) in new (string?, string)[] { (null, "All"), ("item", "Items"), ("spell", "Spells"), ("symbol", "Symbols") })
            {
                var button = Button(label, () => SelectIconCategory(kind));
                button.Tag = kind; button.Margin = new(1, 0, 1, 0); button.Padding = new(4, 3, 4, 3);
                IconCategories.Children.Add(button);
            }
        }
        IconSearch.Clear();
        Error("");
        Heading.Text = "Choose an icon";
        Body.Visibility = W.Visibility.Collapsed;
        IconBrowser.Visibility = W.Visibility.Visible;
        SelectIconCategory(null);
        IconSearch.Focus();
    }

    private void LeaveIconBrowser()
    {
        CancelIconWork();
        iconMatches = null; chooseIcon = null;
        IconTiles.Children.Clear();
        IconBrowser.Visibility = W.Visibility.Collapsed;
        Body.Visibility = W.Visibility.Visible;
        Heading.Text = Title;
        iconOpener?.Focus(); iconOpener = null;
    }

    private void ApplyIcon(string? key) { chooseIcon?.Invoke(key); LeaveIconBrowser(); }
    private void ClearIcon(object sender, W.RoutedEventArgs e) => ApplyIcon(null);

    private void ChooseIconFolder(object sender, W.RoutedEventArgs e)
    {
        try
        {
            if (!EqIconInstallation.ChooseFolder(this)) return;
            Catalogs.Remove(currentArtwork());
            RefreshIcons();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
            or System.Text.Json.JsonException or InvalidOperationException)
        {
            CancelIconWork();
            IconStatus.Text = ex.Message;
        }
    }

    private void SelectIconCategory(string? kind)
    {
        iconKind = kind;
        foreach (C.Button button in IconCategories.Children)
        {
            bool active = Equals(button.Tag, kind);
            button.SetResourceReference(C.Control.BorderBrushProperty, active ? "Accent" : "Line");
            button.SetResourceReference(C.Control.ForegroundProperty, active ? "Accent" : "Text");
            button.FontWeight = active ? W.FontWeights.SemiBold : W.FontWeights.Normal;
        }
        RefreshIcons();
    }

    private void IconSearchChanged(object sender, C.TextChangedEventArgs e)
    {
        if (chooseIcon is null || IconBrowser.Visibility != W.Visibility.Visible) return;
        RefreshIcons(debounce: true);
    }

    private void NextIcons(object sender, W.RoutedEventArgs e) { iconPage++; RefreshIcons(keepMatches: true); }
    private void PreviousIcons(object sender, W.RoutedEventArgs e) { iconPage--; RefreshIcons(keepMatches: true); }

    private void CancelIconWork()
    {
        iconWork?.Cancel(); iconWork?.Dispose(); iconWork = null;
    }

    private void RefreshIcons(bool keepMatches = false, bool debounce = false)
    {
        CancelIconWork();
        if (!keepMatches) { iconMatches = null; iconPage = 0; }
        string query = IconSearch.Text.Trim();
        IconSearchHint.Visibility = IconSearch.Text.Length == 0 ? W.Visibility.Visible : W.Visibility.Collapsed;
        PreviousIconPage.IsEnabled = NextIconPage.IsEnabled = false;
        if (iconKind == "symbol")
        {
            IconTiles.Children.Clear();
            foreach (var symbol in OverlayButtonIcons.Choices.Where(s => s.Id is not null &&
                s.Label.Contains(query, StringComparison.OrdinalIgnoreCase)))
            {
                var tile = IconTile(symbol.Id!, symbol.Label, symbol.Label, "", IconLabel(symbol.Symbol));
                IconTiles.Children.Add(tile);
            }
            IconStatus.Text = IconTiles.Children.Count == 0 ? "No matching symbols." : "";
            IconTiles.IsEnabled = true; IconScroll.ScrollToTop();
            return;
        }
        IconTiles.IsEnabled = false;
        IconStatus.Text = "Loading icons…";
        iconWork = new();
        _ = LoadIconPage(query, iconKind, iconPage, iconMatches, debounce, iconWork.Token);
    }

    private async Task LoadIconPage(string query, string? kind, int page, IReadOnlyList<EqIconSearchResult>? matches,
        bool debounce, CancellationToken token)
    {
        try
        {
            if (debounce) await Task.Delay(160, token).ConfigureAwait(false);
            var result = await Task.Run(() =>
            {
                var art = currentArtwork();
                var catalog = readIconCatalog(art);
                token.ThrowIfCancellationRequested();
                var found = matches ?? catalog.Search(query, kind);
                var tiles = found.Skip(page * IconsPerPage).Take(IconsPerPage).Select(entry =>
                {
                    token.ThrowIfCancellationRequested();
                    return (entry, image: art.Get(entry.Key));
                }).ToArray();
                return (found, tiles, artAvailable: art.Keys().Count > 0);
            }, token).ConfigureAwait(false);
            if (token.IsCancellationRequested) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested) return;
                iconMatches = result.found;
                IconTiles.Children.Clear();
                foreach (var (entry, image) in result.tiles)
                {
                    string family = entry.Kind == "item" ? "Item" : "Spell";
                    string id = $"{family} #{entry.Id}";
                    string label = entry.NameCount > 0 ? entry.Name : id;
                    var tile = IconTile(entry.Key, entry.NameCount > 0 ? $"{id} · {label}" : id, label, entry.NameCount > 0 ? id : "",
                        image is null ? IconLabel("—") : IconImage(image, 40), available: image is not null);
                    if (image is null)
                    {
                        tile.ToolTip = $"{id} · No artwork in this slot";
                        C.ToolTipService.SetShowOnDisabled(tile, true);
                        W.Automation.AutomationProperties.SetHelpText(tile, "No artwork in this slot");
                    }
                    else tile.ToolTip = entry.NameCount > 1
                        ? $"{label}\n{id} · {entry.NameCount:N0} names use this icon" : entry.NameCount > 0 ? $"{label}\n{id}" : id;
                    IconTiles.Children.Add(tile);
                }
                IconTiles.IsEnabled = true;
                int total = iconMatches.Count;
                int start = page * IconsPerPage;
                IconStatus.Text = total > 0 ? $"{start + 1:N0}–{start + result.tiles.Length:N0} of {total:N0}" :
                    result.artAvailable ? "No matching icons." : "Choose your EverQuest folder to load game icons.";
                PreviousIconPage.IsEnabled = page > 0;
                NextIconPage.IsEnabled = start + result.tiles.Length < total;
                IconScroll.ScrollToTop();
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested || Dispatcher.HasShutdownStarted) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested) return;
                IconTiles.Children.Clear();
                IconStatus.Text = "Could not load icons. " + ex.Message;
            });
        }
    }

    private C.Button IconTile(string key, string accessibleName, string label, string detail, W.FrameworkElement preview, bool available = true)
    {
        var tile = Button(accessibleName, available ? () => ApplyIcon(key) : null);
        tile.IsEnabled = available;
        tile.Padding = new(2, 5, 2, 4); tile.Margin = new(2);
        tile.HorizontalContentAlignment = W.HorizontalAlignment.Stretch;
        if (currentIcon == key) tile.SetResourceReference(C.Control.BorderBrushProperty, "Accent");
        var stack = new C.StackPanel();
        stack.Children.Add(preview);
        stack.Children.Add(new C.TextBlock { Text = label, TextAlignment = W.TextAlignment.Center, Margin = new(1, 3, 1, 0),
            TextTrimming = W.TextTrimming.CharacterEllipsis, FontSize = 12 });
        if (detail.Length > 0)
        {
            var caption = new C.TextBlock { Text = detail, TextAlignment = W.TextAlignment.Center, FontSize = 10 };
            caption.SetResourceReference(C.TextBlock.ForegroundProperty, "Muted");
            stack.Children.Add(caption);
        }
        tile.Content = stack;
        return tile;
    }
}
