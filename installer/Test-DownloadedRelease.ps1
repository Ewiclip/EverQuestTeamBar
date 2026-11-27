#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ManifestPath,
    [Parameter(Mandatory)][string]$ManifestSignaturePath,
    [Parameter(Mandatory)][string]$InstallerPath,
    # Supply the app's pinned trust configuration, never a value from the download.
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string[]]$AllowedManifestSignerSha256
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseChecks.ps1')
Add-Type -AssemblyName System.Security.Cryptography.Pkcs
$manifestBytes = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $ManifestPath).Path)
$content = [Security.Cryptography.Pkcs.ContentInfo]::new($manifestBytes)
$cms = [Security.Cryptography.Pkcs.SignedCms]::new($content, $true)
$cms.Decode([IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $ManifestSignaturePath).Path))
if ($cms.SignerInfos.Count -ne 1) { throw 'Expected exactly one release-manifest signer.' }
if (-not $cms.Detached -or [Convert]::ToBase64String($cms.ContentInfo.Content) -cne [Convert]::ToBase64String($manifestBytes)) { throw 'Expected a detached signature over the exact manifest bytes.' }
$signer = $cms.SignerInfos[0]
if ($signer.DigestAlgorithm.Value -notin @('2.16.840.1.101.3.4.2.1', '2.16.840.1.101.3.4.2.2', '2.16.840.1.101.3.4.2.3')) { throw 'Release metadata requires a SHA-256, SHA-384 or SHA-512 digest.' }
$certificate = $signer.Certificate
if ($null -eq $certificate -or $certificate.GetCertHashString([Security.Cryptography.HashAlgorithmName]::SHA256) -notin $AllowedManifestSignerSha256) { throw 'Release manifest is signed by an unapproved publisher.' }
$now = [DateTimeOffset]::UtcNow
if ($certificate.NotBefore.ToUniversalTime() -gt $now.UtcDateTime -or $certificate.NotAfter.ToUniversalTime() -le $now.UtcDateTime) { throw 'The trusted metadata signing certificate is not currently valid.' }
foreach ($extension in $certificate.Extensions) {
    if ($extension -is [Security.Cryptography.X509Certificates.X509KeyUsageExtension] -and
        ($extension.KeyUsages -band [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature) -eq 0) { throw 'The metadata signing certificate does not allow digital signatures.' }
}
# The independently compiled fingerprint is the trust anchor. This also supports a
# dedicated long-lived self-signed metadata certificate kept in a protected signer.
$cms.CheckSignature($true)
$json = Get-Content -LiteralPath $ManifestPath -Raw
if (-not (Test-Json -Json $json -SchemaFile (Join-Path $PSScriptRoot 'release-manifest.schema.json'))) { throw 'Invalid release manifest.' }
$manifest = $json | ConvertFrom-Json
$published = [DateTimeOffset]::Parse($manifest.publishedAt)
$expires = [DateTimeOffset]::Parse($manifest.expiresAt)
if ($published -gt $now.AddMinutes(5) -or $expires -le $now -or $expires -le $published -or $expires -gt $published.AddDays(30)) { throw 'Release metadata is expired, future-dated or has an excessive validity period.' }
$inventory = @{}
foreach ($file in $manifest.files) {
    if ($file.path -match '(^|/)\.\.(/|$)' -or [IO.Path]::IsPathRooted($file.path) -or $inventory.ContainsKey($file.path)) { throw 'Unsafe or duplicate release file path.' }
    $inventory[$file.path] = $true
}
$installer = Get-Item -LiteralPath $InstallerPath
if ($installer.Length -ne $manifest.installer.bytes -or (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash -ne $manifest.installer.sha256) {
    throw 'Downloaded installer length or SHA-256 does not match the authenticated manifest.'
}
$signature = Get-AuthenticodeSignature -LiteralPath $InstallerPath
if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or
    $signature.SignerCertificate.GetCertHashString([Security.Cryptography.HashAlgorithmName]::SHA256) -ne $manifest.installer.publisherSha256) { throw 'Installer Authenticode does not match the trusted manifest publisher.' }
Write-Output 'Release manifest and installer signature, size and hash are valid. Nothing was installed.'
