using C = System.Windows.Controls;
using W = System.Windows;
namespace TeamBar;

internal sealed partial class TeamBarApplication
{
    // Shares the existing runtime suite's isolated controller and synthetic clients.
    // Exercise the actual host, close handler, reopen path, and Exit button together.
    internal static void TestSettingsLifetime(W.Application app, string profile, string controller,
        DetectedClient client, IUserSettingsStore preferences, AppInputSession session, Action<bool, string> check)
    {
        check(Current is null && SpellOverlayManagerWindow.CurrentManager is null && Backend.WaitForNativeShutdown().IsCompleted,
            "Settings lifetime fixture starts without a product host, spell manager, or native game controller.");
        int created = 0;
        using var host = new TeamBarApplication(app, (selectedProfile, selectedController) =>
        {
            created++;
            return new SetupWindow(selectedProfile, selectedController, () => new([client], []),
                reviewOnly: true, userSettings: preferences) { ShowActivated = false };
        });
        Current = host;
        try
        {
            host.ShowSetup(profile, controller);
            var setup = host.setups[Path.GetFullPath(profile)];
            bool closed = false;
            setup.Closed += (_, _) => closed = true;
            var footer = (W.FrameworkElement)setup.FindName("ApplicationLifetimeFooter");
            var exit = (C.Button)setup.FindName("ExitTeamBarButton");
            check(footer.IsVisible && exit.IsVisible && exit.IsEnabled,
                "Hosted settings expose the close-to-tray explanation and explicit Exit Team Bar action.");
            setup.Close();
            check(!closed && !setup.IsVisible && host.setups.Count == 1 && session.Ready
                && ReferenceEquals(AppInputSession.Current, session),
                "The actual settings close handler hides the window and preserves its active input runtime.");
            host.ShowSetup(profile, controller);
            check(created == 1 && ReferenceEquals(host.setups[Path.GetFullPath(profile)], setup) && setup.IsVisible,
                "Reopening hosted settings reuses the hidden window and its draft state.");
            // This suite uses an external input-free fixture controller, whose Dispose
            // joins synchronously. There is no native cleanup task to delay the click.
            exit.RaiseEvent(new W.RoutedEventArgs(C.Primitives.ButtonBase.ClickEvent));
            check(closed && host.setups.Count == 0 && session.IsClosed && AppInputSession.Current is null,
                "Clicking Exit Team Bar closes hosted settings and stops the application input runtime.");
        }
        finally
        {
            foreach (var setup in host.setups.Values.ToArray()) setup.CloseForExit();
            Current = null;
        }
    }
}
