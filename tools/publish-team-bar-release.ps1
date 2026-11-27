#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$')][string]$Version,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+\.\d+$')][string]$FileVersion,
    [Parameter(Mandatory)][string]$NativeDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$NativeRuntimeDirectory,
    [string]$DotNetPath = 'dotnet',
    [ValidateSet('beta', 'stable')][string]$Channel = 'stable',
    [uri]$ReleaseFeedBaseUrl,
    [ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ReleaseManifestSignerSha256
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $repo 'installer/ReleaseChecks.ps1')
$native = (Resolve-Path -LiteralPath $NativeDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a fresh output directory. Publishing never cleans or overwrites an existing build.' }
if ($output.StartsWith($native.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Publish output must be separate from the maintained native runtime.' }
foreach ($name in @('TeamBridge.dll', 'TeamControllerHost.dll')) {
    $null = Get-SafeChildPath -Root $native -RelativePath $name
}
if ($ReleaseFeedBaseUrl -and ($ReleaseFeedBaseUrl.Scheme -ne 'https' -or $ReleaseFeedBaseUrl.UserInfo -or $ReleaseFeedBaseUrl.Query -or $ReleaseFeedBaseUrl.Fragment)) { throw 'Release feed URL must be HTTPS without credentials, queries or fragments.' }
if ([bool]$ReleaseFeedBaseUrl -ne [bool]$ReleaseManifestSignerSha256) { throw 'Configure both the update feed URL and its trusted manifest signer, or omit both for manual updates.' }
$dotnet = (Get-Command $DotNetPath -ErrorAction Stop).Source
$sdkVersion = & $dotnet --version
if ($LASTEXITCODE -ne 0 -or $sdkVersion -notmatch '^10\.0\.\d+$') { throw 'Publish with a supported .NET 10 LTS SDK.' }
New-Item -ItemType Directory -Path $output | Out-Null
$payload = Join-Path $output 'payload'
$project = Join-Path $repo 'probes/FloatingHotbar/FloatingHotbar.csproj'
$arguments = @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $payload,
    '--configfile', (Join-Path $repo 'probes/FloatingHotbar/NuGet.Config'), '-p:TeamBarReleaseBuild=true',
    ('-p:Version=' + $Version), ('-p:FileVersion=' + $FileVersion),
    '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-p:DebugSymbols=false', '-p:DebugType=None', '-p:IncludeSourceRevisionInInformationalVersion=false',
    ('-p:ReleaseChannel=' + $Channel), ('-p:NativeBridgeHash=' + (Get-FileHash -LiteralPath (Join-Path $native 'TeamBridge.dll')).Hash),
    ('-p:NativeCollectorHash=' + (Get-FileHash -LiteralPath (Join-Path $native 'TeamControllerHost.dll')).Hash))
if ($ReleaseFeedBaseUrl) {
    $arguments += @(('-p:ReleaseFeedBaseUrl=' + $ReleaseFeedBaseUrl.AbsoluteUri), ('-p:ReleaseManifestSignerSha256=' + $ReleaseManifestSignerSha256))
}
& $dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'Self-contained release publication failed; the incomplete output was retained for diagnosis.' }
$assetsFile = & $dotnet msbuild $project -nologo -p:Configuration=Release -p:RuntimeIdentifier=win-x64 -p:TeamBarReleaseBuild=true -getProperty:ProjectAssetsFile
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the package folders for the published runtime packs.' }
& (Join-Path $repo 'installer/Copy-DotNetRuntimeNotices.ps1') -PayloadDirectory $payload -ProjectAssetsFile $assetsFile -SdkPackDirectory (Join-Path (Split-Path -Parent $dotnet) 'packs')
foreach ($name in @('TeamBridge.dll', 'TeamControllerHost.dll')) { Copy-Item -LiteralPath (Join-Path $native $name) -Destination $payload }
# Bundle only imported redistributable dependencies, preserving the exact native pair.
# Windows 10/11 provide the UCRT. Do not ship SDK debug runtimes or arbitrary system DLLs.
$pending = [Collections.Generic.Queue[string]]::new()
$pending.Enqueue('TeamBridge.dll'); $pending.Enqueue('TeamControllerHost.dll')
$seen = @{}
while ($pending.Count -gt 0) {
    $name = $pending.Dequeue()
    if ($seen.ContainsKey($name)) { continue }
    $seen[$name] = $true
    foreach ($dependency in @(Get-PeImportNames (Join-Path $payload $name))) {
        if ($dependency -notmatch '^(vcruntime|msvcp|concrt|vcomp)\d+.*\.dll$') { continue }
        if ($dependency -match '(?i)d\.dll$') { throw "Debug native runtime is not distributable: $dependency" }
        $target = Join-Path $payload $dependency
        if (-not (Test-Path -LiteralPath $target)) {
            if (-not $NativeRuntimeDirectory) { throw "Supply -NativeRuntimeDirectory for the app-local Microsoft redistributable dependency $dependency." }
            $runtime = Get-SafeChildPath -Root $NativeRuntimeDirectory -RelativePath $dependency
            $signature = Get-AuthenticodeSignature -LiteralPath $runtime
            if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') { throw "Expected a valid Microsoft-signed redistributable: $runtime" }
            Copy-Item -LiteralPath $runtime -Destination $target
        }
        $pending.Enqueue($dependency)
    }
}
$paths = @(Get-ChildItem -LiteralPath $payload -Recurse -File | ForEach-Object { [IO.Path]::GetRelativePath($payload, $_.FullName).Replace('\', '/') })
($paths + 'installed-files.txt' | Sort-Object) -replace '/', '\' | Set-Content -LiteralPath (Join-Path $payload 'installed-files.txt') -Encoding ascii
$files = @(Get-ChildItem -LiteralPath $payload -Recurse -File | Sort-Object FullName | ForEach-Object {
    @{ path = [IO.Path]::GetRelativePath($payload, $_.FullName).Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash; bytes = $_.Length }
})

$record = Get-Content -LiteralPath (Join-Path $repo 'installer/release-readiness.example.json') -Raw | ConvertFrom-Json
$record.appVersion = $Version; $record.fileVersion = $FileVersion; $record.files = $files
$recordPath = Join-Path $output 'release-readiness.json'
$record | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $recordPath -Encoding utf8NoBOM
$null = Assert-TeamBarInventory -ReadinessPath $recordPath -PayloadDirectory $payload
Write-Output "Published Team Bar payload to $payload"
Write-Output "Inventory: $recordPath"
