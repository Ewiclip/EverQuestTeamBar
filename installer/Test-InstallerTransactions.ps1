#Requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$IsccPath, [Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$iscc = (Resolve-Path -LiteralPath $IsccPath).Path
$root = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $root) { throw 'Fixture output must be a new directory; tests never clear existing data.' }
New-Item -ItemType Directory -Path $root | Out-Null
$results = [Collections.Generic.List[object]]::new()
function Assert-Fixture([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
foreach ($scenario in @('fixture-target-override-blocked', 'first-install', 'legacy-data-import', 'managed-update-game-open', 'running-team-bar-exits', 'running-team-bar-refuses', 'bridge-change-game-open', 'host-change-game-open', 'crt-change-game-open', 'crt-removal-game-open', 'failed-health-rollback', 'interrupted-update-blocked')) {
    $case = Join-Path $root $scenario
    $payload = Join-Path $case 'payload'; $app = Join-Path $case 'app'; $data = Join-Path $case 'data'
    New-Item -ItemType Directory -Path $payload,$app,(Join-Path $data 'profiles') | Out-Null
    $legacyData = Join-Path $case 'legacy-data'
    New-Item -ItemType Directory -Path (Join-Path $legacyData 'profiles'),(Join-Path $legacyData 'Account'),(Join-Path $legacyData 'Contributions') | Out-Null
    'legacy profile' | Set-Content -LiteralPath (Join-Path $legacyData 'profiles/team.json')
    'old token' | Set-Content -LiteralPath (Join-Path $legacyData 'Account/session.dat')
    'contribution preferences' | Set-Content -LiteralPath (Join-Path $legacyData 'Contributions/settings.json')
    'preserved profile' | Set-Content -LiteralPath (Join-Path $data 'profiles/team.json')
    'preserved settings' | Set-Content -LiteralPath (Join-Path $data 'user-settings.json')
    'private secret must not enter backups' | Set-Content -LiteralPath (Join-Path $data 'credentials.json')
    # Synthetic values only. Account is normally beside profiles; also exercise
    # misplaced credentials so a broad profile backup cannot propagate them.
    $credentialFiles = @('Account/session.dat', 'profiles/Account/session.dat', 'profiles/aCcOuNt/other.json',
        'profiles/session.dat', 'profiles/session.dat.saved', 'profiles/credentials.json', 'profiles/credentials.bin',
        'profiles/signing-material.current-user.dpapi', 'profiles/private-key.PEM', 'profiles/publisher.PFX',
        'profiles/publisher.p12', 'profiles/private.key', 'profiles/buttons/nested/signing.dpapi')
    foreach ($relative in $credentialFiles) {
        $credentialPath = Join-Path $data $relative
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $credentialPath) | Out-Null
        'fixture-only credential; preserve source, exclude backup' | Set-Content -LiteralPath $credentialPath
    }
    'public trust only' | Set-Content -LiteralPath (Join-Path $data 'profiles/public-settings.json')
    $names = @('TeamBar.exe', 'TeamBridge.dll', 'TeamControllerHost.dll', 'vcruntime140.dll', 'new-runtime.dll')
    foreach ($name in $names) { ('new-' + $name) | Set-Content -LiteralPath (Join-Path $payload $name) }
    if ($scenario -eq 'legacy-data-import') { Rename-Item -LiteralPath $data -NewName 'standby-fixture-data' }
    if ($scenario -eq 'crt-removal-game-open') { $names = @($names | Where-Object { $_ -ne 'vcruntime140.dll' }); Remove-Item -LiteralPath (Join-Path $payload 'vcruntime140.dll') }
    ($names + 'installed-files.txt') | Set-Content -LiteralPath (Join-Path $payload 'installed-files.txt') -Encoding ascii
    $bridgeTime = [DateTime]::UtcNow.AddDays(-1)
    if ($scenario -ne 'first-install') {
        'old-managed' | Set-Content -LiteralPath (Join-Path $app 'TeamBar.exe')
        'old-retired-file' | Set-Content -LiteralPath (Join-Path $app 'retired-runtime.dll')
        foreach ($name in @('TeamBridge.dll', 'TeamControllerHost.dll', 'vcruntime140.dll')) {
            ('new-' + $name) | Set-Content -LiteralPath (Join-Path $app $name)
            (Get-Item -LiteralPath (Join-Path $app $name)).LastWriteTimeUtc = $bridgeTime
        }
        @('TeamBar.exe','TeamBridge.dll','TeamControllerHost.dll','vcruntime140.dll','retired-runtime.dll','installed-files.txt') | Set-Content -LiteralPath (Join-Path $app 'installed-files.txt') -Encoding ascii
    }
    if ($scenario -match 'game-open') { 'synthetic EQ process' | Set-Content -LiteralPath (Join-Path $case 'eqgame.exe.running') }
    if ($scenario -match '^running-team-bar' -or $scenario -eq 'bridge-change-game-open') {
        'synthetic Team Bar' | Set-Content -LiteralPath (Join-Path $case 'TeamBar.exe.running')
    }
    if ($scenario -eq 'running-team-bar-exits') { 'allow' | Set-Content -LiteralPath (Join-Path $case 'allow-exit') }
    if ($scenario -eq 'bridge-change-game-open') { 'old-native' | Set-Content -LiteralPath (Join-Path $app 'TeamBridge.dll') }
    if ($scenario -eq 'host-change-game-open') { 'old-native' | Set-Content -LiteralPath (Join-Path $app 'TeamControllerHost.dll') }
    if ($scenario -eq 'crt-change-game-open') { 'old-native' | Set-Content -LiteralPath (Join-Path $app 'vcruntime140.dll') }
    if ($scenario -eq 'failed-health-rollback') { 'fail' | Set-Content -LiteralPath (Join-Path $case 'fail-health') }
    if ($scenario -eq 'interrupted-update-blocked') {
        $interrupted = Join-Path $case 'install-backups/interrupted'
        New-Item -ItemType Directory -Path $interrupted | Out-Null
        'installing' | Set-Content -LiteralPath (Join-Path $interrupted 'state.txt')
        $app | Set-Content -LiteralPath (Join-Path $interrupted 'install-path.txt')
    }
    $files = @(Get-ChildItem -LiteralPath $payload -File)
    $fileLines = foreach ($file in $files) {
        $line = 'Source: "' + $file.FullName + '"; DestDir: "{app}"; Flags: ignoreversion'
        if ($file.Name -match '^(TeamBridge|TeamControllerHost|vcruntime140)\.dll$') { $line += '; Check: InstallNativeFile(''' + $file.Name + ''', ''' + (Get-FileHash -LiteralPath $file.FullName).Hash + ''')' }
        $line
    }
    $fileLines | Set-Content -LiteralPath (Join-Path $case 'files.iss')
    @('procedure LoadReleaseInventory;', 'begin') + @($files | ForEach-Object { '  ReleasePaths.Add(''' + $_.Name + '''); ReleaseHashes.Add(''' + (Get-FileHash -LiteralPath $_.FullName).Hash + ''');' }) + @('end;') | Set-Content -LiteralPath (Join-Path $case 'inventory.iss')
    $compileArgs = @('/Qp', ('/DInstallerFixtureRoot=' + $case), ('/DPayloadDir=' + $payload), '/DAppVersion=0.1.0-beta.1', '/DFileVersion=0.1.0.0',
        ('/DOutputDir=' + $case), ('/DVerifiedFilesInclude=' + (Join-Path $case 'files.iss')), ('/DVerifiedInventoryInclude=' + (Join-Path $case 'inventory.iss')), (Join-Path $PSScriptRoot 'team-bar.iss'))
    & $iscc @compileArgs | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Fixture compilation failed: $scenario" }
    $installer = Join-Path $case 'TeamBar-0.1.0-beta.1-win-x64-Setup.exe'
    $installTarget = if ($scenario -eq 'fixture-target-override-blocked') { Join-Path $case 'forbidden-app' } else { $app }
    $process = Start-Process -FilePath $installer -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/DIR="' + $installTarget + '"'),('/LOG="' + (Join-Path $case 'setup.log') + '"')) -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(30000)) { throw "Fixture installer did not finish within 30 seconds (not terminated): $scenario" }
    $process.Refresh()
    $expectedSuccess = $scenario -in @('first-install', 'legacy-data-import', 'managed-update-game-open', 'running-team-bar-exits')
    Assert-Fixture (($process.ExitCode -eq 0) -eq $expectedSuccess) "Unexpected installer exit code $($process.ExitCode): $scenario"
    if ($scenario -match '^running-team-bar') {
        Assert-Fixture (Test-Path -LiteralPath (Join-Path $case 'exit-requested')) 'Setup did not request a graceful exit.'
        Assert-Fixture ((Test-Path -LiteralPath (Join-Path $case 'TeamBar.exe.running')) -ne $expectedSuccess) 'Running process guard was lost.'
        if (-not $expectedSuccess) {
            Assert-Fixture (-not (Test-Path -LiteralPath (Join-Path $case 'install-backups'))) 'Refused shutdown started a transaction.'
        }
    }
    if ($scenario -eq 'bridge-change-game-open') {
        Assert-Fixture (-not (Test-Path -LiteralPath (Join-Path $case 'exit-requested'))) 'An otherwise blocked install asked Team Bar to exit.'
    }
    if ($scenario -eq 'fixture-target-override-blocked') {
        Assert-Fixture ((Get-Content -LiteralPath (Join-Path $case 'setup.log') -Raw).Contains('Fixture target must exactly match its compiled workspace app directory.')) 'Fixture target guard did not reject the override.'
        Assert-Fixture (-not (Test-Path -LiteralPath (Join-Path $case 'install-backups'))) 'Rejected fixture target created a backup transaction.'
        Assert-Fixture (@(Get-ChildItem -LiteralPath $installTarget -Recurse -File -ErrorAction SilentlyContinue).Count -eq 0) 'Rejected fixture target received files.'
    }
    $expectedManaged = if ($expectedSuccess) { 'new-TeamBar.exe' } else { 'old-managed' }
    Assert-Fixture ((Get-Content -LiteralPath (Join-Path $app 'TeamBar.exe') -Raw).Trim() -eq $expectedManaged) "Managed binary was not preserved/restored: $scenario"
    if ($scenario -eq 'legacy-data-import') {
        Assert-Fixture ((Get-Content -LiteralPath (Join-Path $data 'profiles/team.json') -Raw).Trim() -eq 'legacy profile') 'Missing migrated legacy library.'
        Assert-Fixture ((Get-Content -LiteralPath (Join-Path $data 'Contributions/settings.json') -Raw).Trim() -eq 'contribution preferences') 'Missing migrated contribution settings.'
        Assert-Fixture (-not (Test-Path -LiteralPath (Join-Path $data 'Account'))) 'Retired account files reached the new data directory.'
        Assert-Fixture (Test-Path -LiteralPath (Join-Path $legacyData 'Account/session.dat')) 'The source legacy directory was modified.'
        $data = Join-Path $case 'standby-fixture-data'
    }
    Assert-Fixture ((Get-Content -LiteralPath (Join-Path $data 'profiles/team.json') -Raw).Trim() -eq 'preserved profile') "Profile changed: $scenario"
    Assert-Fixture ((Get-Content -LiteralPath (Join-Path $data 'credentials.json') -Raw).Trim() -eq 'private secret must not enter backups') "Credentials changed: $scenario"
    foreach ($relative in $credentialFiles) {
        Assert-Fixture ((Get-Content -LiteralPath (Join-Path $data $relative) -Raw).Trim() -eq 'fixture-only credential; preserve source, exclude backup') "Credential source changed: $relative ($scenario)"
    }
    if ($scenario -eq 'managed-update-game-open') {
        Assert-Fixture ((Get-Item -LiteralPath (Join-Path $app 'TeamBridge.dll')).LastWriteTimeUtc -eq $bridgeTime) 'Identical bridge timestamp changed.'
        Assert-Fixture ((Get-Item -LiteralPath (Join-Path $app 'TeamControllerHost.dll')).LastWriteTimeUtc -eq $bridgeTime) 'Identical host timestamp changed.'
        Assert-Fixture ((Get-Item -LiteralPath (Join-Path $app 'vcruntime140.dll')).LastWriteTimeUtc -eq $bridgeTime) 'Identical C++ runtime timestamp changed.'
        Assert-Fixture (-not (Test-Path -LiteralPath (Join-Path $app 'retired-runtime.dll'))) 'Obsolete inventoried binary was not retired.'
    }
    if ($scenario -eq 'failed-health-rollback') {
        Assert-Fixture (-not (Test-Path -LiteralPath (Join-Path $app 'new-runtime.dll'))) 'Rollback left a new runtime binary.'
        Assert-Fixture (Test-Path -LiteralPath (Join-Path $app 'retired-runtime.dll')) 'Rollback lost a previous runtime binary.'
        $states = @(Get-ChildItem -LiteralPath (Join-Path $case 'install-backups') -Filter state.txt -Recurse | ForEach-Object { (Get-Content -LiteralPath $_.FullName -Raw).Trim() })
        Assert-Fixture (@($states | Where-Object { $_ -like 'rolled-back*' }).Count -eq 1) 'Rollback did not leave a recovery receipt.'
    }
    $backupRoot = Join-Path $case 'install-backups'
    if (Test-Path -LiteralPath $backupRoot) {
        Assert-Fixture (@(Get-ChildItem -LiteralPath $backupRoot -Filter credentials.json -Recurse).Count -eq 0) 'Credentials leaked into backups.'
        foreach ($backup in @(Get-ChildItem -LiteralPath $backupRoot -Directory)) {
            foreach ($relative in $credentialFiles) {
                Assert-Fixture (-not (Test-Path -LiteralPath (Join-Path $backup.FullName ('user-data/' + $relative)))) "Credential entered backup: $relative ($scenario)"
            }
        }
        if (($expectedSuccess -and $scenario -ne 'legacy-data-import') -or $scenario -eq 'failed-health-rollback') {
            $publicFiles = @(Get-ChildItem -LiteralPath $backupRoot -Filter public-settings.json -Recurse)
            Assert-Fixture ($publicFiles.Count -eq 1 -and (Get-Content -LiteralPath $publicFiles[0].FullName -Raw).Trim() -eq 'public trust only') 'Public trust metadata was not preserved in the profile backup.'
            Assert-Fixture (@(Get-ChildItem -LiteralPath $backupRoot -Filter team.json -Recurse).Count -eq 1) 'Saved profile was not backed up.'
        }
    }
    $results.Add(@{ scenario = $scenario; passed = $true; exitCode = $process.ExitCode })
    Write-Output "PASS $scenario"
}
@{ passed = $true; scope = 'Actual Inno transaction code with workspace-only files, synthetic process states and synthetic health receipts. No installed apps, registry, shortcuts, user-data roots or game processes are touched. This is not clean-machine acceptance.'; cases = $results.ToArray() } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'results.json') -Encoding utf8NoBOM
