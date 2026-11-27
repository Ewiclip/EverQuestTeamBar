#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BackupDirectory,
    [Parameter(Mandatory)][string]$ExpectedInstallDirectory,
    [Parameter(Mandatory)][switch]$ConfirmBinaryRestore
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseChecks.ps1')
if (-not $ConfirmBinaryRestore) { throw 'Explicit -ConfirmBinaryRestore is required. User-data backups are never restored by this command.' }
$backup = (Resolve-Path -LiteralPath $BackupDirectory).Path
$installed = [IO.Path]::GetFullPath($ExpectedInstallDirectory).TrimEnd('\', '/')
$recorded = (Get-Content -LiteralPath (Get-SafeChildPath $backup 'install-path.txt') -Raw).Trim()
if (-not $installed.Equals($recorded, [StringComparison]::OrdinalIgnoreCase)) { throw 'Expected install directory does not match the recorded transaction target.' }
$state = (Get-Content -LiteralPath (Get-SafeChildPath $backup 'state.txt') -Raw).Trim()
if ($state -notin @('installing', 'rollback-incomplete')) { throw "Backup is not an interrupted or failed rollback transaction: $state" }
if (@(Get-Process -Name TeamBar -ErrorAction SilentlyContinue).Count -gt 0) { throw 'Exit Team Bar before restoring its binaries.' }
function Assert-UnlinkedPath([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if (((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Linked recovery destination: $current" }
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ($parent -eq $current) { break }; $current = $parent
    }
}
Assert-UnlinkedPath $installed
$transactions = @(Get-Content -LiteralPath (Get-SafeChildPath $backup 'transaction-files.txt'))
$previous = @{}
foreach ($line in @(Get-Content -LiteralPath (Get-SafeChildPath $backup 'previous-hashes.txt'))) {
    $parts = $line.Split('|')
    if ($parts.Count -ne 2 -or $parts[1] -notmatch '^[A-Fa-f0-9]{64}$' -or $previous.ContainsKey($parts[0])) { throw 'Invalid backup hash inventory.' }
    $previous[$parts[0]] = $parts[1]
}
$seen = @{}; $operations = @()
foreach ($relative in $transactions) {
    if ($relative -notmatch '^[A-Za-z0-9_ .\\-]+$' -or $relative.Contains('..') -or [IO.Path]::IsPathRooted($relative) -or $seen.ContainsKey($relative)) { throw "Unsafe or duplicate recovery file: $relative" }
    $seen[$relative] = $true
    $destination = [IO.Path]::GetFullPath((Join-Path $installed $relative))
    if (-not $destination.StartsWith($installed + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Recovery destination escaped the installation directory.' }
    Assert-UnlinkedPath $destination
    $source = $null
    if ($previous.ContainsKey($relative)) {
        $source = Get-SafeChildPath $backup ('binaries/' + $relative.Replace('\', '/'))
        if ((Get-FileHash -LiteralPath $source).Hash -ne $previous[$relative]) { throw "Backup hash mismatch: $relative" }
    }
    $exists = Test-Path -LiteralPath $destination -PathType Leaf
    $changes = if ($source) { -not $exists -or (Get-FileHash -LiteralPath $destination).Hash -ne $previous[$relative] } else { $exists }
    if ($changes) { $operations += @{ relative = $relative; source = $source; destination = $destination; exists = $exists } }
}
foreach ($relative in $previous.Keys) { if (-not $seen.ContainsKey($relative)) { throw 'Backup files are missing from the transaction inventory.' } }
if (@($operations | Where-Object { $_.relative -match '(?i)^(TeamBridge|TeamControllerHost|(?:vcruntime|msvcp|concrt|vcomp)[^\\]*)\.dll$' }).Count -gt 0 -and
    @(Get-Process -Name eqgame -ErrorAction SilentlyContinue).Count -gt 0) { throw 'Close EverQuest before restoring changed native or C++ runtime libraries.' }
$displaced = Join-Path $backup ('recovery-displaced-' + [Guid]::NewGuid().ToString('N'))
foreach ($operation in $operations) {
    if ($operation.exists) {
        $saved = Join-Path $displaced $operation.relative
        New-Item -ItemType Directory -Path (Split-Path $saved -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $operation.destination -Destination $saved
        if ((Get-FileHash -LiteralPath $operation.destination).Hash -ne (Get-FileHash -LiteralPath $saved).Hash) { throw 'Cannot verify the displaced binary backup.' }
    }
}
foreach ($operation in $operations) {
    Assert-UnlinkedPath $operation.destination
    if ($operation.source) {
        New-Item -ItemType Directory -Path (Split-Path $operation.destination -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $operation.source -Destination $operation.destination -Force
        if ((Get-FileHash -LiteralPath $operation.destination).Hash -ne $previous[$operation.relative]) { throw 'Restored binary failed verification. Recovery remains incomplete.' }
    } else { Remove-Item -LiteralPath $operation.destination }
}
'recovered-manually; user data was not restored or changed' | Set-Content -LiteralPath (Join-Path $backup 'state.txt') -Encoding ascii
Write-Output "Restored the recorded binaries to $installed. User data was untouched; displaced binaries remain in $displaced. Rerun Setup to finish or repair installation registration."
