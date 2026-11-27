using W = System.Windows;
using M = System.Windows.Media;

namespace TeamBar;

internal static class ThemePalette
{
    private static readonly (string Key, string Dark, string Light)[] Colors =
    [
        ("AppBackground", "#10171E", "#F3F6F8"),
        ("Text", "#EEF3F7", "#182F3D"),
        ("Surface", "#18222C", "#FFFFFF"),
        ("Field", "#111B24", "#F8FAFB"),
        ("Line", "#2D3D49", "#C9D5DD"),
        ("Muted", "#A2B2C1", "#506675"),
        ("Accent", "#7CDCC5", "#176B5A"),
        ("ButtonSurface", "#202D38", "#EAF0F3"),
        ("OnAccent", "#102A28", "#FFFFFF"),
        ("HoverSurface", "#2A4649", "#D9ECE7"),
        ("SelectedOption", "#244039", "#D5EAE3"),
        ("PopupSurface", "#1C2934", "#FFFFFF"),
        ("PopupBorder", "#455B6C", "#A3B6C1"),
        ("HoverBorder", "#587466", "#4D8978"),
        ("SelectedSurface", "#213B39", "#DEF0E9"),
        ("SelectedBorder", "#426E63", "#6A9F8C"),
        ("SwapTargetSurface", "#F2CB60", "#FFE48C"),
        ("SwapTargetBorder", "#FFE7A2", "#986B13"),
        ("SwapTargetStar", "#362800", "#423100"),
        ("CardSurface", "#131E27", "#F8FAFB"),
        ("CheckBorder", "#54697A", "#718D9C"),
        ("CardSelectedBorder", "#426D61", "#729E8D"),
        ("CardSelected", "#1C302F", "#E7F3ED"),
        ("SegmentSurface", "#101A22", "#EDF2F4"),
        ("SegmentSelected", "#31433F", "#D2E8DF"),
        ("SegmentText", "#B4F0DD", "#175B49"),
        ("Scrollbar", "#3D535F", "#9AAFBA"),
        ("ScrollbarHover", "#72938D", "#557D72"),
        ("HeaderLine", "#283844", "#D7E1E6"),
        ("BrandSurface", "#28463F", "#D4EBE1"),
        ("PanelLine", "#293A46", "#D4DFE5"),
        ("Sidebar", "#151F28", "#EBF1F4"),
        ("WarningSurface", "#393328", "#FFF5DF"),
        ("WarningBorder", "#716041", "#D5B777"),
        ("WarningText", "#E8D0A3", "#765213"),
        ("PreviewSurface", "#152D2B", "#E8F4EE"),
        ("PreviewBorder", "#31524B", "#ADCFBE"),
        ("Success", "#A0D3C4", "#25654F"),
        ("PreviewCaption", "#7BAFA2", "#4B7565"),
        ("PreviewMuted", "#9FBEB5", "#4D6B60"),
        ("PreviewArrow", "#7EA69B", "#4A7964"),
        ("KeySurface", "#23433D", "#D1E9DE"),
        ("KeyBorder", "#4E776A", "#80AD96"),
        ("KeyText", "#AAE8D4", "#1A5740"),
        ("PreviewWarning", "#D5BC91", "#795317"),
        ("PreviewHint", "#92B5A9", "#4A6A5B"),
        ("Unsaved", "#E3C58F", "#7E5B18"),
        ("Error", "#F0B7A6", "#A13F2B"),
    ];
    internal static string Color(string key, AppearanceTheme theme)
    {
        var entry = Colors.Single(c => c.Key == key);
        return theme == AppearanceTheme.Light ? entry.Light : entry.Dark;
    }
    internal static void Apply(W.ResourceDictionary resources, AppearanceTheme theme)
    {
        foreach (var (key, dark, light) in Colors)
        {
            var brush = new M.SolidColorBrush((M.Color)M.ColorConverter.ConvertFromString(theme == AppearanceTheme.Light ? light : dark));
            brush.Freeze(); resources[key] = brush;
        }
    }
}
