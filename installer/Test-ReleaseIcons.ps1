#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ApplicationPath,
    [Parameter(Mandatory)][string]$InstallerPath,
    [Parameter(Mandatory)][string]$IconPath,
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$ManagedAssemblyPath,
    [string]$TrayIconPath
)
$ErrorActionPreference = 'Stop'
if (-not ('TeamBarPackaging.IconResources' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace TeamBarPackaging;
public static class IconResources
{
    private static ushort U16(byte[] bytes, int offset) => BitConverter.ToUInt16(bytes, offset);
    private static uint U32(byte[] bytes, int offset) => BitConverter.ToUInt32(bytes, offset);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static byte[] Slice(byte[] bytes, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset > bytes.Length - count) throw new InvalidDataException("Truncated icon/resource data.");
        return bytes.AsSpan(offset, count).ToArray();
    }
    private static IEnumerable<(uint Name, uint Target)> Entries(byte[] section, int directory)
    {
        Slice(section, directory, 16);
        int count = U16(section, directory + 12) + U16(section, directory + 14);
        for (int index = 0; index < count; index++)
        {
            int entry = checked(directory + 16 + 8 * index);
            Slice(section, entry, 8);
            yield return (U32(section, entry), U32(section, entry + 4));
        }
    }
    private static Dictionary<uint, byte[]> ResourceType(PEReader pe, byte[] section, uint type)
    {
        var result = new Dictionary<uint, byte[]>();
        foreach (var root in Entries(section, 0))
        {
            if (root.Name != type || (root.Target & 0x80000000) == 0) continue;
            foreach (var name in Entries(section, checked((int)(root.Target & 0x7fffffff))))
            {
                // Inno uses a named MAINICON group; C# apphosts use numeric IDs.
                // Preserve either name token as an opaque dictionary key.
                if ((name.Target & 0x80000000) == 0) continue;
                foreach (var language in Entries(section, checked((int)(name.Target & 0x7fffffff))))
                {
                    if ((language.Target & 0x80000000) != 0) throw new InvalidDataException("Unexpected resource directory depth.");
                    int data = checked((int)language.Target);
                    Slice(section, data, 16);
                    int rva = checked((int)U32(section, data));
                    int size = checked((int)U32(section, data + 4));
                    result.TryAdd(name.Name, pe.GetSectionData(rva).GetContent(0, size).ToArray());
                }
            }
        }
        return result;
    }
    public static string[] Verify(string executable, string iconPath)
    {
        byte[] ico = File.ReadAllBytes(iconPath);
        if (U16(ico, 0) != 0 || U16(ico, 2) != 1) throw new InvalidDataException("Invalid source ICO.");
        int count = U16(ico, 4);
        if (count == 0) throw new InvalidDataException("The source ICO is empty.");
        var expected = new List<(byte Width, byte Height, uint Size, string Hash)>();
        for (int index = 0; index < count; index++)
        {
            int entry = checked(6 + index * 16);
            Slice(ico, entry, 16);
            uint size = U32(ico, entry + 8);
            byte[] frame = Slice(ico, checked((int)U32(ico, entry + 12)), checked((int)size));
            expected.Add((ico[entry], ico[entry + 1], size, Hash(frame)));
        }
        using var stream = File.OpenRead(executable);
        using var pe = new PEReader(stream);
        var directory = pe.PEHeaders.PEHeader?.ResourceTableDirectory ?? throw new InvalidDataException("Executable has no PE resource directory.");
        if (directory.Size == 0) throw new InvalidDataException("Executable has no resources.");
        byte[] section = pe.GetSectionData(directory.RelativeVirtualAddress).GetContent().ToArray();
        var images = ResourceType(pe, section, 3);
        var groups = ResourceType(pe, section, 14);
        foreach (var group in groups)
        {
            byte[] data = group.Value;
            if (data.Length < 6 || U16(data, 2) != 1 || U16(data, 4) != count) continue;
            bool matches = true;
            var details = new List<string>();
            for (int index = 0; index < count; index++)
            {
                int entry = checked(6 + index * 14);
                Slice(data, entry, 14);
                var wanted = expected[index];
                if (data[entry] != wanted.Width || data[entry + 1] != wanted.Height || U32(data, entry + 8) != wanted.Size ||
                    !images.TryGetValue(U16(data, entry + 12), out byte[] image) || Hash(image) != wanted.Hash)
                { matches = false; break; }
                details.Add($"{(wanted.Width == 0 ? 256 : wanted.Width)}x{(wanted.Height == 0 ? 256 : wanted.Height)}:{wanted.Hash}");
            }
            if (matches) return details.ToArray();
        }
        throw new InvalidDataException("Executable does not embed an icon group identical to the source artwork: " + executable);
    }
    public static Dictionary<string, string> VerifyManaged(string assembly, string applicationIcon, string trayIcon)
    {
        var expected = new Dictionary<string, string>
        {
            ["TeamBar.Branding.Application.ico"] = Hash(File.ReadAllBytes(applicationIcon)),
            ["TeamBar.Branding.Tray.ico"] = Hash(File.ReadAllBytes(trayIcon))
        };
        using var stream = File.OpenRead(assembly);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var directory = pe.PEHeaders.CorHeader?.ResourcesDirectory ?? throw new InvalidDataException("Assembly has no managed resources.");
        var found = new Dictionary<string, string>();
        foreach (var handle in metadata.ManifestResources)
        {
            var resource = metadata.GetManifestResource(handle);
            string name = metadata.GetString(resource.Name);
            if (!expected.TryGetValue(name, out string wanted)) continue;
            if (!resource.Implementation.IsNil) throw new InvalidDataException("Branding resource is not embedded: " + name);
            byte[] bytes = pe.GetSectionData(checked(directory.RelativeVirtualAddress + (int)resource.Offset)).GetContent().ToArray();
            string hash = Hash(Slice(bytes, 4, checked((int)U32(bytes, 0))));
            if (hash != wanted) throw new InvalidDataException("Embedded branding resource differs from its source: " + name);
            found.Add(name, hash);
        }
        if (found.Count != expected.Count) throw new InvalidDataException("The app assembly is missing embedded application or tray artwork.");
        return found;
    }
}
'@
}
$application = (Resolve-Path -LiteralPath $ApplicationPath).Path
$installer = (Resolve-Path -LiteralPath $InstallerPath).Path
$icon = (Resolve-Path -LiteralPath $IconPath).Path
if (-not $ManagedAssemblyPath) { $ManagedAssemblyPath = [IO.Path]::ChangeExtension($application, '.dll') }
if (-not $TrayIconPath) { $TrayIconPath = Join-Path (Split-Path -Parent $icon) 'TeamBarTray.ico' }
$appFrames = [TeamBarPackaging.IconResources]::Verify($application, $icon)
$setupFrames = [TeamBarPackaging.IconResources]::Verify($installer, $icon)
$managedResources = [TeamBarPackaging.IconResources]::VerifyManaged((Resolve-Path -LiteralPath $ManagedAssemblyPath).Path, $icon, (Resolve-Path -LiteralPath $TrayIconPath).Path)
$result = [ordered]@{
    passed = $true
    method = 'Read-only PE resource parsing; each icon frame matched byte-for-byte to the shared ICO source. No executable was launched.'
    sourceIconSha256 = (Get-FileHash -LiteralPath $icon).Hash
    application = @{ path = $application; sha256 = (Get-FileHash -LiteralPath $application).Hash; frames = $appFrames }
    installer = @{ path = $installer; sha256 = (Get-FileHash -LiteralPath $installer).Hash; frames = $setupFrames }
    managedResources = $managedResources
}
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM
Write-Output "Verified $($appFrames.Count) branded icon frames in both TeamBar.exe and Setup.exe, plus embedded application and tray artwork."
