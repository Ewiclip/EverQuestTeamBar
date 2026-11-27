#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PayloadDirectory,
    [Parameter(Mandatory)][string]$ProjectAssetsFile,
    [Parameter(Mandatory)][string]$SdkPackDirectory
)
$ErrorActionPreference = 'Stop'
$deps = Get-Content -LiteralPath (Join-Path $PayloadDirectory 'TeamBar.deps.json') -Raw | ConvertFrom-Json -AsHashtable
$assets = Get-Content -LiteralPath $ProjectAssetsFile -Raw | ConvertFrom-Json -AsHashtable
$selected = $deps.targets[$deps.runtimeTarget.name]
$copies = [ordered]@{}
foreach ($runtime in @('Microsoft.NETCore.App.Runtime.win-x64', 'Microsoft.WindowsDesktop.App.Runtime.win-x64')) {
    $keyPattern = '^runtimepack\.' + [regex]::Escape($runtime) + '/(10\.0\.\d+)$'
    $matchesInTarget = @($selected.Keys | Where-Object { $_ -cmatch $keyPattern })
    if ($matchesInTarget.Count -ne 1 -or $deps.libraries[$matchesInTarget[0]].type -ne 'runtimepack') {
        throw "Expected exactly one selected $runtime runtime pack in TeamBar.deps.json."
    }
    $version = $matchesInTarget[0].Split('/')[1]
    $roots = @(Join-Path $SdkPackDirectory $runtime $version)
    foreach ($folder in $assets.packageFolders.Keys) {
        $roots += Join-Path $folder $runtime.ToLowerInvariant() $version
    }
    $pack = $roots | Where-Object { Test-Path -LiteralPath $_ -PathType Container } | Select-Object -First 1
    if (!$pack) { throw "The selected runtime pack $runtime/$version was not found in the SDK or restore package folders." }
    $contents = @(Get-ChildItem -LiteralPath $pack -File)
    foreach ($kind in @('LICENSE', 'THIRD-PARTY-NOTICES')) {
        $pattern = if ($kind -eq 'LICENSE') { '^LICENSE(?:\.txt)?$' } else { '^THIRD[-_ ]?PARTY[-_ ]?NOTICES(?:\.txt)?$' }
        $found = @($contents | Where-Object Name -Match $pattern)
        # The Windows Desktop pack currently has its own license but no separate notices file.
        if (!$found.Count -and $kind -eq 'THIRD-PARTY-NOTICES' -and $runtime -eq 'Microsoft.WindowsDesktop.App.Runtime.win-x64') { continue }
        if ($found.Count -ne 1 -or $found[0].Length -le 0) { throw "Missing or ambiguous $kind in $runtime/$version." }
        $copies["$runtime-$kind.txt"] = $found[0].FullName
    }
}
$destination = [IO.Directory]::CreateDirectory((Join-Path $PayloadDirectory 'notices'))
foreach ($pair in $copies.GetEnumerator()) {
    [IO.File]::Copy($pair.Value, (Join-Path $destination.FullName $pair.Key), $false)
}
