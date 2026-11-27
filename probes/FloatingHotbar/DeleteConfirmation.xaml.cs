using W = System.Windows;
namespace TeamBar;

internal sealed record DeleteRequest(string Heading, string Name, string Explanation, string Verb = "delete");

public partial class DeleteConfirmation : W.Window
{
    internal SlideConfirmation Slide { get; }
    internal DeleteConfirmation(W.Window owner, DeleteRequest request)
    {
        Owner = owner; Resources.MergedDictionaries.Add(owner.Resources); InitializeComponent();
        Icon = AppBranding.WindowIcon;
        Heading.Text = request.Heading; ItemName.Text = request.Name; Explanation.Text = request.Explanation;
        Title = request.Heading;
        Slide = new(request.Verb); SlideHost.Content = Slide;
        Slide.Confirmed += () => DialogResult = true;
        Deactivated += (_, _) => Slide.Cancel();
        Closed += (_, _) => Slide.Cancel();
        Loaded += (_, _) => CancelButton.Focus();
    }
    internal static bool Ask(W.Window owner, DeleteRequest request) => new DeleteConfirmation(owner, request).ShowDialog() == true;
    private void Cancel_Click(object sender, W.RoutedEventArgs e) { Slide.Cancel(); DialogResult = false; }
    internal void Export(string path)
    {
        UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)DialogRoot.ActualWidth, (int)DialogRoot.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(DialogRoot); var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = new FileStream(path, FileMode.CreateNew); encoder.Save(file);
    }
}
