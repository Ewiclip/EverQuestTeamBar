using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TeamBar;

internal sealed record ReleaseUpdateConfiguration(Uri FeedBase, string ManifestSignerSha256, string Channel,
    string CurrentVersion, string NativeBridgeHash, string NativeCollectorHash)
{
    internal static string InstalledVersion => typeof(ReleaseUpdateConfiguration).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    private static readonly Dictionary<string, string> metadata = typeof(ReleaseUpdateConfiguration).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(value => value.Key, value => value.Value ?? "", StringComparer.Ordinal);

    internal static bool GitHubNoticesEnabled =>
        string.Equals(metadata.GetValueOrDefault("GitHubReleaseNotices"), "true", StringComparison.OrdinalIgnoreCase) &&
        metadata.GetValueOrDefault("ReleaseChannel") == "beta" &&
        string.IsNullOrWhiteSpace(metadata.GetValueOrDefault("ReleaseFeedBaseUrl")) &&
        string.IsNullOrWhiteSpace(metadata.GetValueOrDefault("ReleaseManifestSignerSha256"));

    // Trust is compiled into the signed application. Neither user preferences nor the feed can change it.
    internal static ReleaseUpdateConfiguration? ReadAssembly()
    {
        var feed = metadata.GetValueOrDefault("ReleaseFeedBaseUrl", "");
        var signer = metadata.GetValueOrDefault("ReleaseManifestSignerSha256", "");
        if (string.IsNullOrWhiteSpace(feed) && string.IsNullOrWhiteSpace(signer)) return null;
        var result = new ReleaseUpdateConfiguration(new Uri(feed, UriKind.Absolute), signer,
            metadata.GetValueOrDefault("ReleaseChannel", ""), InstalledVersion, metadata.GetValueOrDefault("NativeBridgeHash", ""),
            metadata.GetValueOrDefault("NativeCollectorHash", ""));
        result.Validate();
        return result;
    }

    internal void Validate()
    {
        ReleaseUpdateManifest.ValidateHttps(FeedBase);
        if (!FeedBase.AbsolutePath.EndsWith('/') || Channel is not ("beta" or "stable")
            || !ReleaseUpdateManifest.IsHash(ManifestSignerSha256)
            || !ReleaseUpdateManifest.IsHash(NativeBridgeHash) || !ReleaseUpdateManifest.IsHash(NativeCollectorHash))
            throw new InvalidDataException("This build does not have a complete, trusted update configuration.");
        _ = ReleaseUpdateVersion.Parse(CurrentVersion);
    }

    internal Uri AllowedDownload(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) throw new InvalidDataException("The update URL is invalid.");
        ReleaseUpdateManifest.ValidateHttps(uri);
        if (!string.Equals(uri.IdnHost, FeedBase.IdnHost, StringComparison.OrdinalIgnoreCase)
            || uri.Port != FeedBase.Port || !uri.AbsolutePath.StartsWith(FeedBase.AbsolutePath, StringComparison.Ordinal))
            throw new InvalidDataException("The update URL is outside the trusted release location.");
        return uri;
    }
}

internal sealed record ReleaseUpdateInstaller(string Url, string Sha256, long Bytes, string PublisherSha256);
internal sealed record ReleaseUpdateFile(string Path, string Sha256, long Bytes);
internal sealed record ReleaseUpdateManifest(int SchemaVersion, string Product, string Channel, string Version,
    DateTimeOffset PublishedAt, DateTimeOffset ExpiresAt, string NativeBridgeVersion,
    int NativeProtocol, string ReleaseNotes, ReleaseUpdateInstaller Installer, ReleaseUpdateFile[] Files)
{
    internal const int ManifestLimit = 2 * 1024 * 1024;
    internal const int SignatureLimit = 128 * 1024;
    internal const long InstallerLimit = 1024L * 1024 * 1024;
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16, PropertyNameCaseInsensitive = false };

    internal static ReleaseUpdateManifest Parse(byte[] bytes, ReleaseUpdateConfiguration configuration, DateTimeOffset now)
    {
        if (bytes.Length == 0 || bytes.Length > ManifestLimit) throw new InvalidDataException("The update manifest is too large or empty.");
        using (var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 })) RejectDuplicateProperties(document.RootElement);
        var result = JsonSerializer.Deserialize<ReleaseUpdateManifest>(bytes, JsonOptions)
            ?? throw new InvalidDataException("The update manifest is empty.");
        result.Validate(configuration, now);
        return result;
    }

    internal void Validate(ReleaseUpdateConfiguration configuration, DateTimeOffset now)
    {
        if (SchemaVersion != 1 || Product != "team-bar" || Channel != configuration.Channel || NativeProtocol != 1)
            throw new InvalidDataException("This release uses an unsupported product, channel, or protocol.");
        _ = ReleaseUpdateVersion.Parse(Version);
        if (Channel == "stable" && Version.Contains('-')) throw new InvalidDataException("A stable feed cannot offer a prerelease build.");
        if (PublishedAt > now.AddMinutes(5) || ExpiresAt <= now || ExpiresAt <= PublishedAt || ExpiresAt - PublishedAt > TimeSpan.FromDays(30))
            throw new InvalidDataException("The release manifest has expired or has an invalid publication window.");
        if (string.IsNullOrWhiteSpace(NativeBridgeVersion) || NativeBridgeVersion.Length > 80
            || string.IsNullOrWhiteSpace(ReleaseNotes) || ReleaseNotes.Length > 10000)
            throw new InvalidDataException("The release description is invalid.");
        if (Installer is null || !IsHash(Installer.Sha256) || !IsHash(Installer.PublisherSha256) || Installer.Bytes is <= 0 or > InstallerLimit)
            throw new InvalidDataException("The update installer has an invalid size or hash.");
        var url = configuration.AllowedDownload(Installer.Url);
        if (!url.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The update must be a signed Windows installer.");
        if (Files is null || Files.Length is < 4 or > 4096) throw new InvalidDataException("The release file inventory is invalid.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files)
        {
            if (file is null || !IsSafeRelativePath(file.Path) || !paths.Add(file.Path) || !IsHash(file.Sha256) || file.Bytes is <= 0 or > InstallerLimit)
                throw new InvalidDataException("The release file inventory contains an unsafe path, duplicate, hash, or size.");
        }
        foreach (string required in new[] { "TeamBar.exe", "TeamBar.dll", "TeamBridge.dll", "TeamControllerHost.dll" })
            if (!paths.Contains(required)) throw new InvalidDataException("The release is missing a required product file.");
    }

    internal bool RequiresEverQuestClosed(ReleaseUpdateConfiguration configuration)
    {
        try { return RequiresEverQuestClosed(configuration, ReadInstalledRuntimeHashes(AppContext.BaseDirectory)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return true; } // An unreadable native dependency cannot establish that the running bridge is unchanged.
    }

    internal bool RequiresEverQuestClosed(ReleaseUpdateConfiguration configuration, IReadOnlyDictionary<string, string> installedRuntimeHashes) =>
        !string.Equals(Files.Single(file => file.Path.Equals("TeamBridge.dll", StringComparison.OrdinalIgnoreCase)).Sha256,
            configuration.NativeBridgeHash, StringComparison.OrdinalIgnoreCase)
        || !string.Equals(Files.Single(file => file.Path.Equals("TeamControllerHost.dll", StringComparison.OrdinalIgnoreCase)).Sha256,
            configuration.NativeCollectorHash, StringComparison.OrdinalIgnoreCase)
        || RuntimeDependenciesChanged(installedRuntimeHashes);

    private bool RuntimeDependenciesChanged(IReadOnlyDictionary<string, string> installed)
    {
        var offered = Files.Where(file => IsNativeRuntimeFile(file.Path)).ToDictionary(file => file.Path, file => file.Sha256, StringComparer.OrdinalIgnoreCase);
        return offered.Count != installed.Count || offered.Any(file => !installed.TryGetValue(file.Key, out string? hash)
            || !string.Equals(hash, file.Value, StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, string> ReadInstalledRuntimeHashes(string directory)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(path);
            if (!IsNativeRuntimeFile(name)) continue;
            using var file = File.OpenRead(path);
            result.Add(name, Convert.ToHexString(SHA256.HashData(file)));
        }
        return result;
    }

    internal static bool IsNativeRuntimeFile(string path) => !path.Contains('/') && !path.Contains('\\')
        && path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
        && new[] { "vcruntime", "msvcp", "concrt", "vcomp" }.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    internal static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    internal static void ValidateHttps(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.IsLoopback
            || Uri.CheckHostName(uri.Host) != UriHostNameType.Dns || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Contains('.') || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.OriginalString.Contains('\\') || uri.AbsolutePath.Contains('%'))
            throw new InvalidDataException("Updates require a trusted public HTTPS release URL without credentials, redirects, or encoded paths.");
    }

    internal static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 240 || path.StartsWith('/') || path.Contains('\\') || path.Contains(':')
            || path.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '/' or '-' or ' '))) return false;
        return path.Split('/').All(part => part.Length > 0 && part is not ("." or "..") && !part.EndsWith('.') && !part.EndsWith(' ')
            && !Regex.IsMatch(part.Split('.')[0], "^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("The update manifest has duplicate fields.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicateProperties(child);
    }
}

internal sealed record ReleaseUpdateVersion(int Major, int Minor, int Patch, string? Prerelease) : IComparable<ReleaseUpdateVersion>
{
    internal static ReleaseUpdateVersion Parse(string? text)
    {
        var match = Regex.Match(text ?? "", @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$", RegexOptions.CultureInvariant);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out int major) || !int.TryParse(match.Groups[2].Value, out int minor)
            || !int.TryParse(match.Groups[3].Value, out int patch)) throw new InvalidDataException("The release version is invalid.");
        string? pre = match.Groups[4].Success ? match.Groups[4].Value : null;
        if (pre?.Split('.').Any(part => part.Length > 1 && part.All(char.IsAsciiDigit) && part.StartsWith('0')) == true)
            throw new InvalidDataException("The release version has an invalid numeric prerelease component.");
        return new(major, minor, patch, pre);
    }
    public int CompareTo(ReleaseUpdateVersion? other)
    {
        if (other is null) return 1;
        foreach (var result in new[] { Major.CompareTo(other.Major), Minor.CompareTo(other.Minor), Patch.CompareTo(other.Patch) })
            if (result != 0) return result;
        if (Prerelease is null || other.Prerelease is null) return Prerelease is null ? other.Prerelease is null ? 0 : 1 : -1;
        string[] left = Prerelease.Split('.'), right = other.Prerelease.Split('.');
        for (int index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            bool a = left[index].All(char.IsAsciiDigit), b = right[index].All(char.IsAsciiDigit);
            int result = a && b ? left[index].Length.CompareTo(right[index].Length) : a != b ? a ? -1 : 1 : 0;
            if (result == 0) result = string.CompareOrdinal(left[index], right[index]);
            if (result != 0) return result;
        }
        return left.Length.CompareTo(right.Length);
    }
}
