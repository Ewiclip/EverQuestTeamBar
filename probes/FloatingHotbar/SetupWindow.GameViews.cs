using W = System.Windows;
namespace TeamBar;

public partial class SetupWindow
{
    private void GameViews_Click(object sender, W.RoutedEventArgs e) => EditGameViewLayouts();

    internal void EditGameViewLayouts(string? initialLayout = null, bool createNew = false)
    {
        void Report(string message, bool error = false)
        {
            if (ScreensView.IsVisible) ScreenFeedback(message, error);
            else Status(message, error);
        }
        try
        {
            if (review || TeamBarApplication.Current is not { } host)
            {
                Report("Window layouts are available from the running Team Bar application.");
                return;
            }
            if (OwnedWindows.OfType<GameViewLayoutWindow>().FirstOrDefault() is { } open)
            {
                open.Activate();
                return;
            }
            var editor = new GameViewLayoutWindow(this, profilePath, () => team.Characters.Select(c => c.Name),
                () => clients.Select(c => c.Client).ToArray(), host, layoutWindows, layoutRoster, initialLayout, createNew);
            if (editor.ShowDialog() == true) Hide();
            else if (host.GameViewStatus.Length > 0) Report(host.GameViewStatus);
        }
        catch (Exception error) { Report(error.Message, true); }
    }
    internal void ApplyTrayLayout(TeamBarApplication host, GameViewLayout layout) =>
        host.ApplyProfileLayout(profilePath, layout, layoutWindows, layoutRoster);
    private readonly Func<GameSession[]> layoutWindows;
    private readonly Func<Discovery> layoutRoster;
}
