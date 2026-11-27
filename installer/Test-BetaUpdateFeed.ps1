#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$SignedFixtureExecutable
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $root) { throw 'Choose a new fixture output directory.' }
$source = (Get-Item -LiteralPath $SignedFixtureExecutable).FullName
$signature = Get-AuthenticodeSignature -LiteralPath $source
if ($signature.Status -ne 'Valid') { throw 'Supply an existing, trusted Authenticode-signed fixture such as dotnet.exe.' }
$publisher = $signature.SignerCertificate.GetCertHashString([Security.Cryptography.HashAlgorithmName]::SHA256)
$siteUrl = 'https://example.github.io/companion/'
$base = $siteUrl + 'updates/beta/'
$hash = (Get-FileHash -LiteralPath $source).Hash
$checks = [Collections.Generic.List[string]]::new()
$prepare = Join-Path $PSScriptRoot 'Stage-BetaUpdateFeed.ps1'
[void][IO.Directory]::CreateDirectory($root)
Add-Type -AssemblyName System.Security.Cryptography.Pkcs
$key = [Security.Cryptography.RSA]::Create(2048)
$request = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=Ephemeral feed fixture', $key, 'SHA256', [Security.Cryptography.RSASignaturePadding]::Pkcs1)
$now = [DateTimeOffset]::UtcNow
$cert = $request.CreateSelfSigned($now.AddDays(-2), $now.AddDays(1))
$pin = $cert.GetCertHashString([Security.Cryptography.HashAlgorithmName]::SHA256)

function New-Bundle([string]$Version, [DateTimeOffset]$Expires) {
    $path = Join-Path $root $Version
    [void][IO.Directory]::CreateDirectory($path)
    $name = "TeamBar-$Version-win-x64-Setup.exe"
    [IO.File]::Copy($source, (Join-Path $path $name))
    $manifest = [ordered]@{
        schemaVersion = 1; product = 'team-bar'; channel = 'beta'; version = $Version
        publishedAt = $now.AddDays(-1).ToString('O'); expiresAt = $Expires.ToString('O')
        nativeBridgeVersion = 'fixture'; nativeProtocol = 1; releaseNotes = 'Feed verification fixture.'
        installer = @{ url = $base + $name; sha256 = $hash; bytes = (Get-Item -LiteralPath $source).Length; publisherSha256 = $publisher }
        files = @('TeamBar.exe', 'TeamBar.dll', 'TeamBridge.dll', 'TeamControllerHost.dll' | ForEach-Object {
            @{ path = $_; sha256 = $hash; bytes = 1 }
        })
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Depth 6))
    $cms = [Security.Cryptography.Pkcs.SignedCms]::new([Security.Cryptography.Pkcs.ContentInfo]::new($bytes), $true)
    $cms.ComputeSignature([Security.Cryptography.Pkcs.CmsSigner]::new($cert))
    [IO.File]::WriteAllBytes((Join-Path $path 'manifest.json'), $bytes)
    [IO.File]::WriteAllBytes((Join-Path $path 'manifest.json.p7s'), $cms.Encode())
    [IO.File]::WriteAllText((Join-Path $path 'private-build-receipt.txt'), 'Local fixture only')
    return $path
}
function Expect-Rejection([string]$Reason, [hashtable]$Changes, [string]$Pattern) {
    $argsToTest = $options.Clone()
    foreach ($k in $Changes.Keys) { $argsToTest[$k] = $Changes[$k] }
    try { & $prepare @argsToTest | Out-Null }
    catch {
        if ($_.Exception.Message -notmatch $Pattern) { throw }
        $checks.Add($Reason)
        return
    }
    throw "Expected rejection for $Reason."
}
try {
    $latest = '0.1.0-beta.3'
    $new = New-Bundle $latest $now.AddDays(1)
    $older = New-Bundle '0.1.0-beta.2' $now.AddDays(1)
    $expired = New-Bundle '0.1.0-beta.1' $now.AddHours(-1)
    $site = Join-Path $root 'pages'
    $options = @{
        Version = $latest; ReleaseDirectory = $new; PreviousReleaseDirectories = @($older, $expired)
        SiteDirectory = $site; SiteBaseUrl = $siteUrl; ManifestSignerSha256 = $pin
    }
    & $prepare @options | Out-Null
    $feed = Join-Path $site 'updates/beta'
    $expected = @('manifest.json', 'manifest.json.p7s', "TeamBar-$latest-win-x64-Setup.exe", 'TeamBar-0.1.0-beta.2-win-x64-Setup.exe')
    $actual = @(Get-ChildItem -LiteralPath $site -Recurse -File)
    if ($actual.Count -ne $expected.Count -or (Compare-Object $actual.Name $expected) -or
        ($actual | Where-Object DirectoryName -ne $feed)) { throw 'Unexpected Pages site inventory.' }
    foreach ($name in $expected) {
        $origin = if ($name.Contains('beta.2')) { $older } else { $new }
        if ((Get-FileHash -LiteralPath (Join-Path $feed $name)).Hash -ne (Get-FileHash -LiteralPath (Join-Path $origin $name)).Hash) {
            throw "Staged file differs from its release input: $name"
        }
    }
    $checks.Add('Published bytes match signed inputs, with the previous valid installer retained and expired or private files omitted.')
    Expect-Rejection 'Fresh artifact directory required' @{} 'new output directory'
    $options.SiteDirectory = Join-Path $root 'rejected-site'
    Expect-Rejection 'Independent metadata signer pin enforced' @{ManifestSignerSha256 = ('A' * 64)} 'unapproved publisher'
    Expect-Rejection 'Exact Pages project path required' @{SiteBaseUrl = 'https://example.github.io/another/'} 'signed installer URL'
    Expect-Rejection 'Release tag must match signed version' @{Version = '0.1.0-beta.4'} 'requested beta feed'
    Expect-Rejection 'Expired current metadata rejected' @{Version = '0.1.0-beta.1'; ReleaseDirectory = $expired} 'expired'
    [IO.File]::AppendAllText((Join-Path $older 'TeamBar-0.1.0-beta.2-win-x64-Setup.exe'), 'tampered')
    Expect-Rejection 'Retained installer is verified too' @{} 'length or SHA-256'
    if ((Test-Path -LiteralPath $options.SiteDirectory) -or (Get-ChildItem -LiteralPath $root -Directory -Filter '.beta-feed-*')) {
        throw 'A failed staging operation exposed a partial feed or left working files.'
    }
    $checks.Add('A failed publication leaves the existing feed intact and cleans up staged files.')
    [pscustomobject]@{ passed = $true; checks = $checks.ToArray(); installed = $false; uploaded = $false } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $root 'results.json') -Encoding utf8NoBOM
    Write-Output "$($checks.Count) beta feed checks passed."
} finally {
    $cert.Dispose()
    $key.Dispose()
}
