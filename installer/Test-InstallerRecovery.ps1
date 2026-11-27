#Requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $root) { throw 'Recovery fixtures require a new output directory.' }
New-Item -ItemType Directory -Path $root | Out-Null
$recoveryScript = Join-Path $PSScriptRoot 'Restore-InstallerBackup.ps1'
$results = [Collections.Generic.List[object]]::new()
function Assert-RecoveryFixture([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Get-FixtureSnapshot([string]$Directory) {
    @(Get-ChildItem -LiteralPath $Directory -File -Recurse | Sort-Object FullName | ForEach-Object {
        [IO.Path]::GetRelativePath($Directory, $_.FullName) + '|' + (Get-FileHash -LiteralPath $_.FullName).Hash
    }) -join "`n"
}
function Invoke-FixtureRecovery([string]$Backup, [string]$Expected, [bool]$GameRunning) {
    # Scope the process probe to this invocation only. The production recovery
    # command has no bypass switch; every file destination is a workspace fixture.
    function Get-Process {
        [CmdletBinding()]
        param([string]$Name)
        if ($Name -eq 'eqgame' -and $GameRunning) { [pscustomobject]@{ ProcessName = 'eqgame'; Id = 12345 } }
    }
    & $recoveryScript -BackupDirectory $Backup -ExpectedInstallDirectory $Expected -ConfirmBinaryRestore | Out-Null
}
foreach ($scenario in @('restore-and-preserve', 'tampered-backup-rejected', 'destination-mismatch-rejected', 'native-restore-game-open-rejected')) {
    $case = Join-Path $root $scenario; $app = Join-Path $case 'app'; $data = Join-Path $case 'user-data'; $backup = Join-Path $case 'backup'
    New-Item -ItemType Directory -Path $app,$data,(Join-Path $backup 'binaries') | Out-Null
    'unchanged profile' | Set-Content -LiteralPath (Join-Path $data 'team.json')
    'unchanged private credential' | Set-Content -LiteralPath (Join-Path $data 'credentials.json')
    foreach ($name in @('TeamBar.exe', 'TeamBridge.dll', 'new-runtime.dll')) { ('partial-new-' + $name) | Set-Content -LiteralPath (Join-Path $app $name) }
    $previous = @('TeamBar.exe', 'TeamBridge.dll', 'retired-runtime.dll')
    foreach ($name in $previous) { ('previous-' + $name) | Set-Content -LiteralPath (Join-Path $backup ('binaries/' + $name)) }
    $previous | Set-Content -LiteralPath (Join-Path $backup 'previous-files.txt')
    $previous | ForEach-Object { $_ + '|' + (Get-FileHash -LiteralPath (Join-Path $backup ('binaries/' + $_))).Hash } | Set-Content -LiteralPath (Join-Path $backup 'previous-hashes.txt')
    ($previous + 'new-runtime.dll') | Set-Content -LiteralPath (Join-Path $backup 'transaction-files.txt')
    $app | Set-Content -LiteralPath (Join-Path $backup 'install-path.txt')
    'installing' | Set-Content -LiteralPath (Join-Path $backup 'state.txt')
    if ($scenario -eq 'tampered-backup-rejected') { 'changed after snapshot' | Set-Content -LiteralPath (Join-Path $backup 'binaries/TeamBridge.dll') }
    $beforeApp = Get-FixtureSnapshot $app; $beforeData = Get-FixtureSnapshot $data
    $expected = if ($scenario -eq 'destination-mismatch-rejected') { Join-Path $case 'other-app' } else { $app }
    $errorMessage = $null
    try { Invoke-FixtureRecovery -Backup $backup -Expected $expected -GameRunning ($scenario -eq 'native-restore-game-open-rejected') }
    catch { $errorMessage = $_.Exception.Message }
    $displaced = @(Get-ChildItem -LiteralPath $backup -Directory -Filter 'recovery-displaced-*')
    if ($scenario -eq 'restore-and-preserve') {
        Assert-RecoveryFixture ($null -eq $errorMessage) "Recovery failed: $errorMessage"
        foreach ($name in $previous) {
            Assert-RecoveryFixture ((Get-Content -LiteralPath (Join-Path $app $name) -Raw).Trim() -ceq ('previous-' + $name)) "Previous binary was not restored: $name"
        }
        Assert-RecoveryFixture (-not (Test-Path -LiteralPath (Join-Path $app 'new-runtime.dll'))) 'A newly introduced binary survived rollback.'
        Assert-RecoveryFixture ($displaced.Count -eq 1) 'Displaced binaries were not retained.'
        Assert-RecoveryFixture ((Get-FixtureSnapshot $displaced[0].FullName) -ceq $beforeApp) 'The displaced backup does not preserve every replaced/removed binary.'
        Assert-RecoveryFixture ((Get-Content -LiteralPath (Join-Path $backup 'state.txt') -Raw).StartsWith('recovered-manually')) 'Recovery did not record completion.'
    } else {
        $expectedError = switch ($scenario) {
            'tampered-backup-rejected' { 'Backup hash mismatch:*' }
            'destination-mismatch-rejected' { 'Expected install directory does not match*' }
            'native-restore-game-open-rejected' { 'Close EverQuest before restoring*' }
        }
        Assert-RecoveryFixture ($null -ne $errorMessage -and $errorMessage -like $expectedError) "Wrong rejection for ${scenario}: $errorMessage"
        Assert-RecoveryFixture ((Get-FixtureSnapshot $app) -ceq $beforeApp) "A rejected recovery modified the installation: $scenario"
        Assert-RecoveryFixture ($displaced.Count -eq 0) "A rejected recovery wrote a displaced backup: $scenario"
        Assert-RecoveryFixture ((Get-Content -LiteralPath (Join-Path $backup 'state.txt') -Raw).Trim() -ceq 'installing') 'A rejected recovery changed transaction state.'
    }
    Assert-RecoveryFixture ((Get-FixtureSnapshot $data) -ceq $beforeData) "User data changed: $scenario"
    $results.Add(@{ scenario = $scenario; passed = $true })
    Write-Output "PASS $scenario"
}
@{ passed = $true; scope = 'Production binary recovery script against workspace-only paths with a scoped process-probe mock. No live apps or user-data roots were changed.'; cases = $results.ToArray() } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'results.json') -Encoding utf8NoBOM
