# Team Bar

Team Bar is a free Windows x64 desktop app for arranging EverQuest windows, floating hotkeys and spell overlays. The app keeps profiles on the local computer. Memory reading and data contributions are optional.

## Beta installers

With automatic checks enabled, Team Bar checks for a newer beta each time you open the app. Choose **Download and install** to download the Windows x64 installer and start Setup. **Skip this version** pauses reminders until a newer beta appears. To install a skipped release, open **Settings → General → Updates** and choose **Check for updates**.

If you are upgrading from beta.4 or beta.5, use **View release on GitHub** to download the newer installer once. [GitHub Releases](https://github.com/Ewiclip/EverQuestTeamBar/releases)

## Source and licensing

This repository contains the desktop app, Screens, the managed sources it compiles, installer and update tooling, and tests. Start with the [illustrated guides](docs/guides/README.md) or the [installer and recovery guide](installer/README.md). The [MIT license](LICENSE) covers the original Team Bar source. The installer includes separate notices for the bundled runtimes.

The native game input components are maintained separately and are required to build the full installer. The optional third-party item-name index is omitted pending redistribution review. Item icons can be selected by ID, and spell names come from the local game installation.

## Build and test

On Windows with the .NET 10 SDK and PowerShell 7.4 or newer, run these commands from this directory. Use fresh output paths for each fixture run.

```powershell
dotnet build probes/FloatingHotbar/FloatingHotbar.csproj -c Release -o artifacts/checks/app
dotnet artifacts/checks/app/TeamBar.dll --game-view-layout-test artifacts/checks/layout.json
dotnet artifacts/checks/app/TeamBar.dll --release-update-test artifacts/checks/updater
dotnet artifacts/checks/app/TeamBar.dll --button-icon-test artifacts/checks/icons.json
```

The fixtures cover layout rules, update verification and the icon picker. A distributable build also needs the native dependencies, an approved signing identity and clean Windows installation and upgrade checks.

## Screens

Use **Settings → Screens** to assign windows to slots. EQ must be in Windowed mode. With **Enable Quick Swap** on, clicking a preview moves that character into the starred slot. With Quick Swap off and **All screens match master resolution** on, Team Bar can forward single left or right clicks to a fixed preview for the original supported game folder. The pointer may move briefly. Use the starred slot for dragging, or turn off both switches to play directly in each slot. **End layout** restores the game windows.

For an alternate EverQuest Live installation, select **Choose EQ folder** in Screens, restart Team Bar, then identify and assign the numbered game windows. Hotkeys, spell input and optional memory reading currently require the original supported game folder listed in the [first-button guide](docs/guides/first-button.md).
