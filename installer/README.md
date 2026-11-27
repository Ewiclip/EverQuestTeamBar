# Team Bar installer and updates

Team Bar has one Windows x64 package. It includes the desktop app, the .NET 10 runtime, and its native dependencies. It runs locally without an account. The installer uses the same app and upgrade identity whether you sign the package or build it unsigned for local testing.

## Build the installer

Use PowerShell 7.4+, a .NET 10 SDK and Inno Setup 6.7.4 or newer. Provide the directory containing the maintained native DLLs and the directory containing their Microsoft-signed C++ redistributable dependencies. Use a fresh output directory.

```powershell
./tools/publish-team-bar-release.ps1 -Version 0.1.0 -FileVersion 0.1.0.0 `
  -NativeDirectory ./artifacts/team-bar-api-input `
  -NativeRuntimeDirectory $nativeRuntimeDirectory `
  -DotNetPath ./artifacts/toolchain/dotnet/dotnet.exe `
  -OutputDirectory ./artifacts/team-bar-release

./installer/Build-Installer.ps1 -Unsigned `
  -PayloadDirectory ./artifacts/team-bar-release/payload `
  -ReadinessPath ./artifacts/team-bar-release/release-readiness.json `
  -IsccPath ./artifacts/toolchain/InnoSetup6/ISCC.exe `
  -OutputDirectory ./artifacts/team-bar-release/setup
```

The output is `TeamBar-<version>-win-x64-Setup.exe`. The build checks the exact inventory, hashes, native dependencies, application identity, and artwork. The installed entry points exclude developer diagnostics, test controllers, and personal data. Building locally leaves installed programs and desktop shortcuts untouched.

The installer’s `notices` directory contains the licenses and third-party notices supplied by the exact .NET Core and Windows Desktop runtime packs selected for this release. Their contents are included in the signed file inventory.

## Installations and data

New installations use `%LOCALAPPDATA%\Programs\Team Bar`. Per-user data lives in `%LOCALAPPDATA%\TeamBar\team-bar`.

- Setup upgrades an existing standard installation in place. If a machine only has the older **Team Bar Internal QA** installation registered, Setup adopts that registration and existing program directory, installs Team Bar, and updates its Start menu entry. Keeping the existing program directory preserves the absolute paths of its native dependencies.
- When the standard data directory is absent, Setup stages a verified copy of the old profiles, spell overlays, preferences and optional EQTradeskills contribution settings before the installed app checks them. Existing standard data takes precedence. The original files remain in `%LOCALAPPDATA%\TeamBar-QA\team-bar`. Settings also provides explicit profile import.
- If Windows lists both old editions, remove **Team Bar Internal QA** through Installed apps and then upgrade **Team Bar**. The old uninstaller retains personal data. Setup checks for this case before writing files.
- Exit Team Bar before installation. If Setup reports that a native component must change while EverQuest is running, close EverQuest before retrying. A managed-only upgrade can proceed while EQ is running when all existing native dependencies match.

Setup records a verified backup in `%LOCALAPPDATA%\TeamBar\install-backups`. It validates the installed file hashes, starts the installed health check, then retires previously inventoried obsolete files. If the health check fails, it restores the previous binaries. When Setup reports an interrupted update, close Team Bar and recover the verified binary inventory using the recorded target directory.

```powershell
./installer/Restore-InstallerBackup.ps1 -BackupDirectory $reportedBackup `
  -ExpectedInstallDirectory $installedDirectory -ConfirmBinaryRestore
```

## Signed releases and automatic updates

For a signed release, sign the native DLLs before publishing. To enable automatic updates, provide `-ReleaseFeedBaseUrl`, `-ReleaseManifestSignerSha256`, and the appropriate `-Channel` when publishing. Omitting the feed leaves updates manual. Sign the final TeamBar.exe and TeamBar.dll and update their recorded inventory hashes. Record passing release checks and their hashed evidence in the readiness file.

Invoke Build-Installer.ps1 with the same payload, readiness file, ISCC and output arguments, then supply `-SignToolPath`, `-PublisherThumbprint`, `-ManifestSignerThumbprint`, `-Channel`, `-DownloadBaseUrl`, and `-ReleaseNotesPath`. The signed build produces the installer, `manifest.json`, and its detached `manifest.json.p7s` signature. The client authenticates the manifest before verifying the installer's publisher, size and hash. Use Test-DownloadedRelease.ps1 against the independently pinned manifest signer before publication.

### Publish a beta update through GitHub

1. In the public repository’s **Settings → Pages**, select **GitHub Actions** as the build source. The beta workflow deploys the entire Pages site. Use a site dedicated to this feed. [GitHub Pages workflow setup](https://docs.github.com/en/pages/getting-started-with-github-pages/using-custom-workflows-with-github-pages)
2. Append `updates/beta/` to the configured Pages site address. A project site normally uses `https://<owner>.github.io/<repository>/updates/beta/`. For a custom domain, use the address shown in Pages settings. Set `-ReleaseFeedBaseUrl` and `-DownloadBaseUrl` to this same address, including the trailing slash, and use `-Channel beta` for both build steps.
3. Under **Settings → Secrets and variables → Actions → Variables**, set `BETA_MANIFEST_SIGNER_SHA256` to the metadata certificate fingerprint supplied to `-ReleaseManifestSignerSha256`. The workflow verifies existing signatures using this public fingerprint. Keep signing keys with the release signer.
4. Push a numbered beta tag such as `v0.1.0-beta.1`. After the Windows checks pass, the workflow opens a draft prerelease for that tag. Attach the signed installer, `manifest.json`, and `manifest.json.p7s`, review the release notes, then publish it. The feed serves the highest published beta version by semantic version. Older installers remain available while their signed metadata is valid, so a client midway through an update can finish.
5. In **Actions → Publish verified beta update feed**, check that the `deploy` job succeeded. The manifest is served at `updates/beta/manifest.json`. For an existing release, running the workflow manually revalidates its assets. Metadata expires after seven days by default, with a maximum of 30 days. Publish a fresh signed release or renew its signed metadata before expiry.

To verify the exact Pages files locally, run the staging command on Windows with the same independently configured fingerprint and the base site address. Use a new output directory for each run.

```powershell
./installer/Stage-BetaUpdateFeed.ps1 -Version '0.1.0-beta.1' -ReleaseDirectory $signedArtifacts `
  -SiteBaseUrl $pagesSiteUrl -ManifestSignerSha256 $trustedFingerprint -SiteDirectory $newOutputDirectory
```

## Verification

Test-ReleaseIcons.ps1 verifies the executable, installer and tray artwork. Test-ReleaseCredentialExclusions.ps1 exercises payload exclusions. Test-InstallerRecovery.ps1 checks backup restoration. Test-InstallerTransactions.ps1 compiles isolated fixture installers for first install, upgrade, rollback, native-file protection, and legacy data import. These use a fixed workspace target and synthetic data. A clean Windows machine is still needed for end-to-end release acceptance.
