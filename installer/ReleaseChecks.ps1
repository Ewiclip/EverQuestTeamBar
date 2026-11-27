Set-StrictMode -Version Latest

function Get-SafeChildPath {
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$RelativePath)
    if ($RelativePath -notmatch '^[A-Za-z0-9_./ -]+$' -or $RelativePath -match '(^|/)\.{1,2}(/|$)|//|[. ](/|$)' -or [IO.Path]::IsPathRooted($RelativePath)) {
        throw "Unsafe release path: $RelativePath"
    }
    $base = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (((Get-Item -LiteralPath $Root).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'A release root cannot be a linked directory.' }
    $candidate = [IO.Path]::GetFullPath((Join-Path $base $RelativePath))
    if (-not $candidate.StartsWith($base, [StringComparison]::OrdinalIgnoreCase)) { throw 'Release path escapes its root.' }
    $current = Get-Item -LiteralPath $candidate -ErrorAction Stop
    while ($null -ne $current -and $current.FullName.StartsWith($base, [StringComparison]::OrdinalIgnoreCase)) {
        if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Linked release path is not allowed: $RelativePath" }
        $current = if ($current -is [IO.DirectoryInfo]) { $current.Parent } else { $current.Directory }
    }
    return $candidate
}

function Assert-PublisherSignature {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string[]]$AllowedPublisherThumbprints)
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or $null -eq $signature.SignerCertificate) {
        throw "A valid Authenticode signature is required: $Path"
    }
    if ($signature.SignerCertificate.Thumbprint -notin $AllowedPublisherThumbprints) { throw "Unapproved release publisher: $Path" }
}

function Get-PeImportNames {
    param([Parameter(Mandatory)][string]$Path)
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 256 -or [BitConverter]::ToUInt16($bytes, 0) -ne 0x5a4d) { throw "Not a PE binary: $Path" }
    $pe = [BitConverter]::ToInt32($bytes, 0x3c)
    if ($pe -lt 64 -or $pe + 256 -gt $bytes.Length -or [BitConverter]::ToUInt32($bytes, $pe) -ne 0x4550) { throw "Invalid PE header: $Path" }
    if ([BitConverter]::ToUInt16($bytes, $pe + 4) -ne 0x8664) { throw "The native payload must be Windows x64: $Path" }
    $sections = [BitConverter]::ToUInt16($bytes, $pe + 6)
    $optionalSize = [BitConverter]::ToUInt16($bytes, $pe + 20)
    if ([BitConverter]::ToUInt16($bytes, $pe + 24) -ne 0x20b) { throw "Expected a PE32+ image: $Path" }
    $importRva = [BitConverter]::ToUInt32($bytes, $pe + 24 + 120)
    if ($importRva -eq 0) { return @() }
    $sectionStart = $pe + 24 + $optionalSize
    $toOffset = {
        param([uint32]$rva)
        for ($index = 0; $index -lt $sections; $index++) {
            $section = $sectionStart + 40 * $index
            $virtualSize = [BitConverter]::ToUInt32($bytes, $section + 8)
            $virtualAddress = [BitConverter]::ToUInt32($bytes, $section + 12)
            $rawSize = [BitConverter]::ToUInt32($bytes, $section + 16)
            if ($rva -ge $virtualAddress -and $rva -lt $virtualAddress + [Math]::Max($virtualSize, $rawSize)) {
                $offset = [BitConverter]::ToUInt32($bytes, $section + 20) + $rva - $virtualAddress
                if ($offset -ge $bytes.Length) { throw "Invalid PE RVA: $Path" }
                return [int]$offset
            }
        }
        throw "Unmapped PE RVA: $Path"
    }
    $descriptor = & $toOffset $importRva
    for ($count = 0; $count -lt 4096; $count++) {
        if ($descriptor + 20 -gt $bytes.Length) { throw "Truncated PE imports: $Path" }
        $nameRva = [BitConverter]::ToUInt32($bytes, $descriptor + 12)
        if ($nameRva -eq 0) { return }
        $nameOffset = & $toOffset $nameRva
        $end = $nameOffset
        while ($end -lt $bytes.Length -and $bytes[$end] -ne 0) { $end++ }
        if ($end -eq $bytes.Length) { throw "Truncated PE import name: $Path" }
        [Text.Encoding]::ASCII.GetString($bytes, $nameOffset, $end - $nameOffset)
        $descriptor += 20
    }
    throw "Excessive PE imports: $Path"
}

function Assert-NativeRuntime {
    param([Parameter(Mandatory)][string]$PayloadDirectory)
    $pending = [Collections.Generic.Queue[string]]::new()
    $pending.Enqueue('TeamBridge.dll'); $pending.Enqueue('TeamControllerHost.dll')
    $seen = @{}
    while ($pending.Count -gt 0) {
        $name = $pending.Dequeue()
        if ($seen.ContainsKey($name)) { continue }
        $seen[$name] = $true
        foreach ($dependency in @(Get-PeImportNames (Join-Path $PayloadDirectory $name))) {
            if ($dependency -match '^(vcruntime|msvcp|concrt|vcomp)\d+.*\.dll$') {
                if ($dependency -match '(?i)d\.dll$') { throw "Debug C++ runtime cannot be shipped: $dependency" }
                if (-not (Test-Path -LiteralPath (Join-Path $PayloadDirectory $dependency) -PathType Leaf)) {
                    throw "Missing app-local Microsoft C++ runtime dependency $dependency required by $name. Supply the matching redistributable DLLs or use a static-runtime native build."
                }
                $pending.Enqueue($dependency)
            }
        }
    }
}

function Assert-ReleasePayload {
    param([Parameter(Mandatory)][string]$PayloadDirectory, [Parameter(Mandatory)]$Files)
    $inventory = @{}
    foreach ($entry in $Files) {
        if ($inventory.ContainsKey($entry.path)) { throw "Duplicate release path: $($entry.path)" }
        $path = Get-SafeChildPath -Root $PayloadDirectory -RelativePath $entry.path
        # Keep personal files and private signing material out of distributable payloads.
        if ($entry.path -match '(?i)(^|/)(profiles|sessions|contributions|account|\.git|\.env)(/|$)|\.(pdb|log|jsonl|ps1|cmd|bat|dpapi|pem|pfx|p12|key)$' -or
            [IO.Path]::GetFileName($path) -match '(?i)^(BarUiTestController|TeamController|.*Tests?|.*Probe)\.exe$|^(team|buttons|user-settings|spell-overlays)\.json$|^session\.dat(?:\..*)?$|^credentials\.(json|bin)$') {
            throw "Developer or personal data cannot be shipped: $($entry.path)"
        }
        if ($entry.sha256 -notmatch '^[A-Fa-f0-9]{64}$' -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Release payload hash does not match inventory: $($entry.path)" }
        $inventory[$entry.path] = $path
    }
    $actual = @(Get-ChildItem -LiteralPath $PayloadDirectory -File -Recurse)
    if ($actual.Count -ne $inventory.Count) { throw 'Payload contains files not present in the approved inventory.' }
    foreach ($file in $actual) {
        $relative = [IO.Path]::GetRelativePath($PayloadDirectory, $file.FullName).Replace('\', '/')
        if (-not $inventory.ContainsKey($relative)) { throw "Unlisted payload file: $relative" }
    }
    foreach ($required in @('TeamBar.exe', 'TeamBar.dll', 'TeamBar.deps.json', 'TeamBar.runtimeconfig.json', 'TeamControllerHost.dll', 'TeamBridge.dll',
        'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'clrjit.dll', 'PresentationNative_cor3.dll', 'wpfgfx_cor3.dll', 'installed-files.txt',
        'notices/Microsoft.NETCore.App.Runtime.win-x64-LICENSE.txt', 'notices/Microsoft.NETCore.App.Runtime.win-x64-THIRD-PARTY-NOTICES.txt',
        'notices/Microsoft.WindowsDesktop.App.Runtime.win-x64-LICENSE.txt')) {
        if (-not $inventory.ContainsKey($required)) { throw "Missing self-contained release file: $required" }
    }
    $runtime = Get-Content -LiteralPath $inventory['TeamBar.runtimeconfig.json'] -Raw | ConvertFrom-Json
    if ($runtime.runtimeOptions.tfm -ne 'net10.0' -or -not $runtime.runtimeOptions.PSObject.Properties['includedFrameworks']) { throw 'Expected a self-contained .NET 10 runtime configuration.' }
    foreach ($framework in $runtime.runtimeOptions.includedFrameworks) {
        if ($framework.version -notmatch '^10\.0\.\d+$') { throw 'All bundled .NET frameworks must be .NET 10 LTS release versions.' }
    }
    Assert-NativeRuntime $PayloadDirectory
    $installedPaths = @(Get-Content -LiteralPath $inventory['installed-files.txt'])
    if ($installedPaths.Count -ne $inventory.Count) { throw 'Installed-file recovery inventory does not match the payload.' }
    $installedSeen = @{}
    foreach ($relative in $installedPaths) {
        $canonical = $relative.Replace('\', '/')
        if (-not $inventory.ContainsKey($canonical) -or $installedSeen.ContainsKey($canonical)) { throw 'Installed-file recovery inventory contains unknown or duplicate paths.' }
        $installedSeen[$canonical] = $true
    }
    $appBytes = [IO.File]::ReadAllBytes($inventory['TeamBar.dll'])
    $appStrings = [Text.Encoding]::Unicode.GetString($appBytes) + [Text.Encoding]::Unicode.GetString($appBytes, 1, $appBytes.Length - 1)
    foreach ($developerSwitch in @('--spell-overlay-test', '--session-fixture', '--identity-scan', '--target-capture', '--native-host-test', '--preview-setup')) {
        if ($appStrings.Contains($developerSwitch)) { throw "Developer entry point remains in the installed assembly: $developerSwitch" }
    }
    return $inventory
}

function Get-TeamBarAssemblyMetadata {
    param([Parameter(Mandatory)][string]$AssemblyPath)
    $stream = [IO.File]::OpenRead($AssemblyPath)
    try {
        $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
        try {
            $reader = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
            $values = @{}
            foreach ($handle in $reader.GetAssemblyDefinition().GetCustomAttributes()) {
                $attribute = $reader.GetCustomAttribute($handle)
                if ($attribute.Constructor.Kind -ne 'MemberReference') { continue }
                $member = $reader.GetMemberReference([Reflection.Metadata.MemberReferenceHandle]$attribute.Constructor)
                if ($member.Parent.Kind -ne 'TypeReference') { continue }
                $type = $reader.GetTypeReference([Reflection.Metadata.TypeReferenceHandle]$member.Parent)
                if ($reader.GetString($type.Name) -ne 'AssemblyMetadataAttribute' -or $reader.GetString($type.Namespace) -ne 'System.Reflection') { continue }
                $blob = $reader.GetBlobReader($attribute.Value)
                if ($blob.ReadUInt16() -ne 1) { throw 'Invalid assembly metadata.' }
                $key = $blob.ReadSerializedString()
                $values.Add($key, $blob.ReadSerializedString())
            }
            return $values
        } finally { $pe.Dispose() }
    } finally { $stream.Dispose() }
}

function Assert-TeamBarInventory {
    param([Parameter(Mandatory)][string]$ReadinessPath, [Parameter(Mandatory)][string]$PayloadDirectory)
    $record = Get-Content -LiteralPath $ReadinessPath -Raw | ConvertFrom-Json
    if ($record.schemaVersion -ne 1 -or $record.product -cne 'team-bar' -or $record.artifactFlavor -cne 'team-bar' -or $record.selfContained -cne $true) {
        throw 'Expected the self-contained Team Bar release payload.'
    }
    if ($record.appVersion -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$' -or $record.fileVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw 'Valid release and Windows file versions are required.' }
    $inventory = Assert-ReleasePayload -PayloadDirectory $PayloadDirectory -Files $record.files
    $metadata = Get-TeamBarAssemblyMetadata $inventory['TeamBar.dll']
    if ($metadata['TeamBarArtifactFlavor'] -cne 'team-bar') { throw 'The compiled application does not match the Team Bar release identity.' }
    return $record
}

function Assert-ReleaseReadiness {
    param(
        [Parameter(Mandatory)][string]$ReadinessPath,
        [Parameter(Mandatory)][string]$PayloadDirectory,
        [Parameter(Mandatory)][string[]]$AllowedPublisherThumbprints
    )
    $payload = (Resolve-Path -LiteralPath $PayloadDirectory).Path
    $readiness = Assert-TeamBarInventory $ReadinessPath $payload
    if ($readiness.readyForDistribution -cne $true) { throw 'Release is not approved for distribution.' }
    if ($readiness.runtimeTfm -ne 'net10.0-windows') { throw 'The release baseline is a supported, patched .NET 10 LTS Windows runtime.' }
    foreach ($check in @('publicEntryPointsOnly',
        'profileMigrationAndRollback', 'gracefulInputShutdown', 'supportedPatchedRuntime', 'cleanWindowsInstallUpgradeUninstall')) {
        if ($readiness.checks.$check -cne $true) { throw "Release prerequisite has not passed: $check" }
    }
    if ($readiness.nativeProtocol -lt 1 -or [string]::IsNullOrWhiteSpace($readiness.nativeBridgeVersion)) {
        throw 'Native compatibility metadata is required.'
    }
    if (@($readiness.evidence).Count -eq 0) { throw 'Attach the actual passing release-test reports to the readiness artifact.' }
    foreach ($evidence in $readiness.evidence) {
        $path = Get-SafeChildPath -Root (Split-Path -Parent (Resolve-Path -LiteralPath $ReadinessPath).Path) -RelativePath $evidence.path
        if ($evidence.sha256 -notmatch '^[A-Fa-f0-9]{64}$' -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $evidence.sha256) {
            throw 'Release-test evidence does not match its recorded hash.'
        }
        $report = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        if ($report.passed -cne $true) { throw 'Release-test evidence does not report success.' }
    }
    foreach ($name in @('TeamBar.exe', 'TeamBar.dll', 'TeamControllerHost.dll', 'TeamBridge.dll')) {
        Assert-PublisherSignature -Path (Join-Path $payload $name) -AllowedPublisherThumbprints $AllowedPublisherThumbprints
    }
    return $readiness
}
