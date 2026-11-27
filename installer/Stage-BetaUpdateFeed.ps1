#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReleaseDirectory,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][uri]$SiteBaseUrl,
    [Parameter(Mandatory)][string]$SiteDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ManifestSignerSha256,
    [string[]]$PreviousReleaseDirectories = @()
)
$ErrorActionPreference = 'Stop'
if (!$SiteBaseUrl.IsAbsoluteUri -or $SiteBaseUrl.Scheme -ne 'https' -or !$SiteBaseUrl.IsDefaultPort -or
    $SiteBaseUrl.IsLoopback -or $SiteBaseUrl.HostNameType -ne [UriHostNameType]::Dns -or
    !$SiteBaseUrl.Host.Contains('.') -or $SiteBaseUrl.Host.EndsWith('.local', [StringComparison]::OrdinalIgnoreCase) -or
    $SiteBaseUrl.UserInfo -or $SiteBaseUrl.Query -or $SiteBaseUrl.Fragment -or
    $SiteBaseUrl.OriginalString.Contains('\') -or $SiteBaseUrl.AbsolutePath.Contains('%')) {
    throw 'Supply the public HTTPS base URL of the GitHub Pages site.'
}
$feedBase = $SiteBaseUrl.AbsoluteUri.TrimEnd('/') + '/updates/beta/'
$target = [IO.Path]::GetFullPath($SiteDirectory)
if (Test-Path -LiteralPath $target) { throw 'Choose a new output directory for the verified Pages artifact.' }
$parent = [IO.Path]::GetDirectoryName($target)
$stage = Join-Path $parent ('.beta-feed-' + [guid]::NewGuid().ToString('N'))
$updates = Join-Path $stage 'updates'
$feed = Join-Path $updates 'beta'
$scratch = Join-Path $stage 'verify'
$createdFiles = [Collections.Generic.List[string]]::new()

function Confirm-BetaVersion([string]$Value) {
    $parsed = $null
    if ($Value.Length -gt 80 -or $Value -cnotmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?\z' -or
        ![semver]::TryParse($Value, [ref]$parsed) -or $Value -cne $parsed.ToString()) {
        throw 'Expected a release version such as 0.1.0-beta.1.'
    }
    return $parsed
}

function Copy-FeedFile([string]$Source, [string]$Destination, [long]$Limit) {
    $item = Get-Item -LiteralPath $Source
    if ($item -isnot [IO.FileInfo] -or $item.Length -le 0 -or $item.Length -gt $Limit) {
        throw "Invalid feed file size: $($item.Name)"
    }
    $createdFiles.Add($Destination)
    [IO.File]::Copy($item.FullName, $Destination, $false)
}

$currentVersion = Confirm-BetaVersion $Version
try {
    [void][IO.Directory]::CreateDirectory($feed)
    [void][IO.Directory]::CreateDirectory($scratch)
    foreach ($directory in @($ReleaseDirectory) + $PreviousReleaseDirectories) {
        $current = $directory -ceq $ReleaseDirectory
        $metadataRoot = if ($current) { $feed } else { $scratch }
        $metadata = Join-Path $metadataRoot 'manifest.json'
        $signature = $metadata + '.p7s'
        Copy-FeedFile (Join-Path $directory 'manifest.json') $metadata 2MB
        $manifest = Get-Content -LiteralPath $metadata -Raw | ConvertFrom-Json
        $releaseVersion = Confirm-BetaVersion $manifest.version
        if ($manifest.product -cne 'team-bar' -or $manifest.channel -cne 'beta' -or $manifest.nativeProtocol -ne 1 -or
            ($current -and $manifest.version -cne $Version) -or (!$current -and $releaseVersion -ge $currentVersion)) {
            throw 'The release metadata does not match the requested beta feed and version.'
        }
        if (!$current -and [DateTimeOffset]$manifest.expiresAt -le [DateTimeOffset]::UtcNow) {
            [IO.File]::Delete($metadata)
            continue
        }
        $installerName = "TeamBar-$($manifest.version)-win-x64-Setup.exe"
        if ($manifest.installer.url -cne $feedBase + $installerName) {
            throw "The signed installer URL must be $feedBase$installerName."
        }
        Copy-FeedFile (Join-Path $directory 'manifest.json.p7s') $signature 128KB
        $installer = Join-Path $feed $installerName
        Copy-FeedFile (Join-Path $directory $installerName) $installer 1GB
        & (Join-Path $PSScriptRoot 'Test-DownloadedRelease.ps1') -ManifestPath $metadata -ManifestSignaturePath $signature `
            -InstallerPath $installer -AllowedManifestSignerSha256 $ManifestSignerSha256
        if (!$current) {
            [IO.File]::Delete($metadata)
            [IO.File]::Delete($signature)
        }
    }
    [IO.Directory]::Delete($scratch)
    [IO.Directory]::Move($stage, $target)
    Write-Output "Verified beta feed staged at $target for $feedBase"
} finally {
    # Only remove paths created by this invocation, leaving any existing destination untouched.
    if ([IO.Directory]::Exists($stage)) {
        foreach ($path in $createdFiles) { [IO.File]::Delete($path) }
        foreach ($path in @($scratch, $feed, $updates, $stage)) {
            if ([IO.Directory]::Exists($path)) { [IO.Directory]::Delete($path) }
        }
    }
}
