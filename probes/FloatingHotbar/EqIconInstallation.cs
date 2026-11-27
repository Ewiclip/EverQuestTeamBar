using System.Text.Json;
using W = System.Windows;

namespace TeamBar;

internal static class EqIconInstallation
{
    private static string DefaultDirectory => Path.GetDirectoryName(MemoryReader.LiveExecutablePath)!;

    internal static string Directory
    {
        get
        {
            try
            {
                var settings = new LocalUserSettingsStore(LocalUserSettingsStore.DefaultPath).Load();
                if (IsLocalPath(settings.IconGameDirectory)) return settings.IconGameDirectory!;
                if (IsLocalPath(settings.ScreenGameDirectory) && HasArtworkLayout(settings.ScreenGameDirectory!))
                    return settings.ScreenGameDirectory!;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            { /* Artwork is optional when preferences cannot be read. */ }
            var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var publicFiles = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
            var candidates = new[] { DefaultDirectory, Path.Combine(programs, "Steam", "steamapps", "common", "EverQuest"),
                Path.Combine(publicFiles, "Daybreak Game Company", "Installed Games", "EverQuest") };
            return candidates.FirstOrDefault(HasArtworkLayout) ?? DefaultDirectory;
        }
    }

    internal static bool IsLocalPath(string? path) => path is { Length: >= 3 } && char.IsAsciiLetter(path[0])
        && path[1] == ':' && path[2] is '\\' or '/' && Path.IsPathFullyQualified(path);

    private static bool HasArtworkLayout(string directory)
    {
        if (!IsLocalPath(directory)) return false;
        string folder = Path.Combine(directory, "uifiles", "default");
        return File.Exists(Path.Combine(folder, "EQUI_SpellIcons.xml")) || File.Exists(Path.Combine(folder, "EQUI_DragItems.xml"))
            || File.Exists(Path.Combine(folder, "EQUI_Animations.xml"));
    }

    internal static bool ChooseFolder(W.Window owner)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose your EverQuest installation",
            InitialDirectory = EqIconArtwork.Shared.GameDirectory };
        if (dialog.ShowDialog(owner) != true) return false;
        string path = Path.GetFullPath(dialog.FolderName);
        if (!HasArtworkLayout(path)) throw new InvalidDataException("Choose the EverQuest folder containing uifiles\\default.");
        new LocalUserSettingsStore(LocalUserSettingsStore.DefaultPath).SaveIconDirectory(path);
        EqIconArtwork.UseDirectory(path);
        return true;
    }
}
