#Requires -Version 7.4
[CmdletBinding(DefaultParameterSetName = 'Production')]
param(
    [Parameter(Mandatory)][string]$PayloadDirectory,
    [Parameter(Mandatory)][string]$ReadinessPath,
    [Parameter(Mandatory, ParameterSetName = 'Production')][string]$SignToolPath,
    [Parameter(Mandatory, ParameterSetName = 'Production')][ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$PublisherThumbprint,
    [Parameter(Mandatory, ParameterSetName = 'Production')][ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$ManifestSignerThumbprint,
    [Parameter(Mandatory, ParameterSetName = 'Production')][ValidateSet('beta', 'stable')][string]$Channel,
    [Parameter(Mandatory, ParameterSetName = 'Production')][uri]$DownloadBaseUrl,
    [Parameter(Mandatory, ParameterSetName = 'Production')][string]$ReleaseNotesPath,
    [Parameter(Mandatory, ParameterSetName = 'Unsigned')][switch]$Unsigned,
    [Parameter(Mandatory)][string]$IsccPath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateRange(1, 30)][int]$ManifestLifetimeDays = 7,
    [uri]$TimestampUrl = 'https://timestamp.digicert.com'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseChecks.ps1')
$payload = (Resolve-Path -LiteralPath $PayloadDirectory).Path
$iscc = (Resolve-Path -LiteralPath $IsccPath).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ($output.StartsWith($payload.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or $output -eq $payload) { throw 'Build output must be outside the immutable release payload.' }
if ($Unsigned) {
    $readiness = Assert-TeamBarInventory -ReadinessPath $ReadinessPath -PayloadDirectory $payload
} else {
    if ($DownloadBaseUrl.Scheme -ne 'https' -or $TimestampUrl.Scheme -ne 'https') { throw 'Download and timestamp URLs must use HTTPS.' }
    if ($DownloadBaseUrl.Query -or $DownloadBaseUrl.Fragment -or $DownloadBaseUrl.UserInfo -or $TimestampUrl.UserInfo) { throw 'Release URLs cannot include credentials, queries, or fragments.' }
    $readiness = Assert-ReleaseReadiness -ReadinessPath $ReadinessPath -PayloadDirectory $payload -AllowedPublisherThumbprints @($PublisherThumbprint)
    $signTool = (Resolve-Path -LiteralPath $SignToolPath).Path
    $notes = Get-Content -LiteralPath $ReleaseNotesPath -Raw
    if ([string]::IsNullOrWhiteSpace($notes) -or $notes.Length -gt 10000) { throw 'Provide nonempty release notes of at most 10,000 characters.' }
}
$setupIcon = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../probes/FloatingHotbar/Assets/TeamBar.ico'))
$iconBytes = [IO.File]::ReadAllBytes($setupIcon)
if ($iconBytes.Length -lt 22 -or [BitConverter]::ToUInt16($iconBytes, 0) -ne 0 -or
    [BitConverter]::ToUInt16($iconBytes, 2) -ne 1 -or [BitConverter]::ToUInt16($iconBytes, 4) -lt 1) {
    throw 'The shared Team Bar Setup/application icon is missing or invalid.'
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
$buildId = [Guid]::NewGuid().ToString('N')
$include = Join-Path $output ('verified-files-' + $buildId + '.iss')
$codeInclude = Join-Path $output ('verified-inventory-' + $buildId + '.iss')
$fileLines = foreach ($file in $readiness.files) {
    $relative = $file.path.Replace('/', '\')
    $destination = Split-Path -Parent $relative
    $line = 'Source: "' + (Join-Path $payload $relative) + '"; DestDir: "{app}' + $(if ($destination) { '\' + $destination } else { '' }) + '"; Flags: ignoreversion'
    if ($relative -match '(?i)^(TeamBridge|TeamControllerHost|(?:vcruntime|msvcp|concrt|vcomp)\d+.*)\.dll$') { $line += '; Check: InstallNativeFile(''' + $relative + ''', ''' + $file.sha256 + ''')' }
    $line
}
$fileLines | Set-Content -LiteralPath $include -Encoding UTF8
@('procedure LoadReleaseInventory;', 'begin') + @($readiness.files | ForEach-Object {
    '  ReleasePaths.Add(''' + $_.path.Replace('/', '\') + '''); ReleaseHashes.Add(''' + $_.sha256 + ''');'
}) + @('end;') | Set-Content -LiteralPath $codeInclude -Encoding UTF8
$bridgeHash = ($readiness.files | Where-Object path -eq 'TeamBridge.dll').sha256
$hostHash = ($readiness.files | Where-Object path -eq 'TeamControllerHost.dll').sha256
$arguments = @('/Qp', ('/DPayloadDir=' + $payload), ('/DAppVersion=' + $readiness.appVersion),
    ('/DFileVersion=' + $readiness.fileVersion), ('/DOutputDir=' + $output),
    ('/DBridgeSha256=' + $bridgeHash), ('/DHostSha256=' + $hostHash),
    ('/DVerifiedFilesInclude=' + $include), ('/DVerifiedInventoryInclude=' + $codeInclude), ('/DSetupIconFile=' + $setupIcon))
if (!$Unsigned) {
    $signCommand = '$q' + $signTool + '$q sign /sha1 ' + $PublisherThumbprint + ' /fd SHA256 /td SHA256 /tr ' + $TimestampUrl.AbsoluteUri + ' $f'
    $arguments += '/DSignedSetup=1'
    $arguments += '/STeamBarRelease=' + $signCommand
}
$arguments += Join-Path $PSScriptRoot 'team-bar.iss'
& $iscc @arguments
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation/signing failed.' }
$setup = Join-Path $output ('TeamBar-' + $readiness.appVersion + '-win-x64-Setup.exe')
if ($Unsigned) {
    $receipt = [ordered]@{ schemaVersion = 1; product = 'team-bar'; appVersion = $readiness.appVersion; installer = @{ path = $setup; sha256 = (Get-FileHash -LiteralPath $setup).Hash; bytes = (Get-Item -LiteralPath $setup).Length }; setupIconSha256 = (Get-FileHash -LiteralPath $setupIcon).Hash; unsigned = $true }
    $receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'installer-build.json') -Encoding utf8NoBOM
    Write-Output "Built unsigned Team Bar installer at $setup"
    return
}
Assert-PublisherSignature -Path $setup -AllowedPublisherThumbprints @($PublisherThumbprint)
$installerPublisherSha256 = (Get-AuthenticodeSignature -LiteralPath $setup).SignerCertificate.GetCertHashString([Security.Cryptography.HashAlgorithmName]::SHA256)
$published = [DateTime]::UtcNow
$manifest = [ordered]@{
    schemaVersion = 1; product = 'team-bar'; channel = $Channel; version = $readiness.appVersion
    publishedAt = $published.ToString('O'); expiresAt = $published.AddDays($ManifestLifetimeDays).ToString('O')
    nativeBridgeVersion = $readiness.nativeBridgeVersion; nativeProtocol = $readiness.nativeProtocol; releaseNotes = $notes.Trim()
    installer = @{ url = $DownloadBaseUrl.AbsoluteUri.TrimEnd('/') + '/' + [IO.Path]::GetFileName($setup); sha256 = (Get-FileHash -LiteralPath $setup).Hash; bytes = (Get-Item -LiteralPath $setup).Length; publisherSha256 = $installerPublisherSha256 }
    files = @($readiness.files | ForEach-Object { @{ path = $_.path; sha256 = $_.sha256; bytes = (Get-Item -LiteralPath (Join-Path $payload $_.path)).Length } })
}
$manifestPath = Join-Path $output 'manifest.json'
$json = $manifest | ConvertTo-Json -Depth 7
if (-not (Test-Json -Json $json -SchemaFile (Join-Path $PSScriptRoot 'release-manifest.schema.json'))) { throw 'Generated manifest does not satisfy the updater contract.' }
[IO.File]::WriteAllText($manifestPath, $json, [Text.UTF8Encoding]::new($false))
Add-Type -AssemblyName System.Security.Cryptography.Pkcs
$certificate = Get-Item -LiteralPath ('Cert:\CurrentUser\My\' + $ManifestSignerThumbprint)
if (-not $certificate.HasPrivateKey) { throw 'The release signing certificate needs its private key in the current-user certificate store.' }
$content = [Security.Cryptography.Pkcs.ContentInfo]::new([IO.File]::ReadAllBytes($manifestPath))
$cms = [Security.Cryptography.Pkcs.SignedCms]::new($content, $true)
$signer = [Security.Cryptography.Pkcs.CmsSigner]::new($certificate)
$signer.DigestAlgorithm = [Security.Cryptography.Oid]::new('2.16.840.1.101.3.4.2.1')
$cms.ComputeSignature($signer)
[IO.File]::WriteAllBytes($manifestPath + '.p7s', $cms.Encode())
Write-Output "Created signed installer and detached-signed release manifest in $output. Nothing was installed or uploaded."
