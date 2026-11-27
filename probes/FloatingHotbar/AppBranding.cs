using System.Windows.Media.Imaging;
using DrawingIcon = System.Drawing.Icon;

namespace TeamBar;

internal static class AppBranding
{
    private const string ApplicationResource = "TeamBar.Branding.Application.ico";
    private const string TrayResource = "TeamBar.Branding.Tray.ico";
    private static readonly Lazy<DrawingIcon> applicationIcon = new(() => LoadIcon(ApplicationResource, new(32, 32)));
    private static readonly Lazy<DrawingIcon> trayIcon = new(() => LoadIcon(TrayResource, System.Windows.Forms.SystemInformation.SmallIconSize));
    private static readonly Lazy<BitmapFrame> windowIcon = new(LoadWindowIcon);

    // Shared icons live for the process lifetime, so closing one tray or form
    // cannot invalidate an icon still used by another surface. Callers don't dispose them.
    internal static DrawingIcon ApplicationIcon => applicationIcon.Value;
    internal static DrawingIcon TrayIcon => trayIcon.Value;
    internal static BitmapFrame WindowIcon => windowIcon.Value;

    private static Stream Open(string resource) => typeof(AppBranding).Assembly.GetManifestResourceStream(resource)
        ?? throw new InvalidDataException("The embedded Team Bar icon is missing: " + resource);

    private static DrawingIcon LoadIcon(string resource, System.Drawing.Size size)
    {
        using var stream = Open(resource);
        using var icon = new DrawingIcon(stream, size);
        return (DrawingIcon)icon.Clone();
    }

    private static BitmapFrame LoadWindowIcon()
    {
        using var stream = Open(ApplicationResource);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames.MaxBy(image => image.PixelWidth * image.PixelHeight)
            ?? throw new InvalidDataException("The embedded Team Bar icon has no image frames.");
        frame.Freeze();
        return frame;
    }
}
