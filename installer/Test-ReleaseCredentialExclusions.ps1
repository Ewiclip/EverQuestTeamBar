#Requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseChecks.ps1')
$root = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $root) { throw 'Fixture output must be a new directory; tests never clear existing data.' }
New-Item -ItemType Directory -Path $root | Out-Null
$results = [Collections.Generic.List[object]]::new()
$rejected = @('Account/session.dat', 'aCcOuNt/other.json', 'nested/Account/session.dat', 'session.dat', 'session.dat.saved',
    'credentials.json', 'credentials.bin', 'signing-material.current-user.dpapi', 'nested/private-key.PEM',
    'publisher.PFX', 'nested/publisher.p12', 'private.key')
$accepted = @('public-settings.json', 'publisher.cer', 'TeamBar.dll')
foreach ($relative in $rejected + $accepted) {
    $case = Join-Path $root ('case-' + $results.Count)
    $path = Join-Path $case $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
    'Synthetic fixture only; no account or signing secrets.' | Set-Content -LiteralPath $path -Encoding utf8NoBOM
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    $entry = @{ path = $relative; sha256 = $hash; bytes = (Get-Item -LiteralPath $path).Length }
    $failure = $null
    try { $null = Assert-ReleasePayload -PayloadDirectory $case -Files @($entry) }
    catch { $failure = $_.Exception.Message }
    $mustReject = $relative -in $rejected
    # Accepted files advance through the real inventory/hash checks and reach
    # the expected missing-runtime error; no native executable is needed.
    $expected = if ($mustReject) { 'Developer or personal data cannot be shipped:*' } else { 'Missing self-contained release file:*' }
    if ($null -eq $failure -or $failure -notlike $expected) { throw "Unexpected release-check result for ${relative}: $failure" }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $hash) { throw 'Release validation changed its source file.' }
    $results.Add(@{ path = $relative; excluded = $mustReject; passed = $true })
}
@{ passed = $true; cases = $results.ToArray(); liveUserDataAccessed = $false; buildsRun = $false } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'results.json') -Encoding utf8NoBOM
Write-Output "PASS $($results.Count) release credential exclusion checks"
