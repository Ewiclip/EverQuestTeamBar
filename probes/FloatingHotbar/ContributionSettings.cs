using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TeamBar;

internal sealed record ContributionDataSelection(bool Items = true, bool Spells = true, bool Recipes = true,
    bool Npcs = false, bool Drops = false, bool Foraged = false, bool Fished = false)
{
    internal bool Any => Items || Spells || Recipes || Npcs || Drops || Foraged || Fished;
    internal bool Includes(string kind) => kind switch { "item" => Items, "spell" => Spells, "recipe" => Recipes,
        "npc" => Npcs, "drop" => Drops, "foraged" => Foraged, "fished" => Fished, _ => false };
}
internal sealed record ContributionSettings(bool Enabled = false, string Endpoint = "", string AccountId = "", bool HasApiKey = false)
{
    public ContributionDataSelection Data { get; init; } = new();
}
internal sealed record ContributionStatus(string Message, int PendingCount = 0, bool IsSending = false,
    bool NeedsAttention = false, DateTimeOffset? LastUploadAtUtc = null);
internal sealed record ContributionCredential(string Endpoint, string AccountId, string Token);

internal static class ContributionStorage
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    internal static string DefaultDirectory => Path.Combine(InstalledAppData.DataDirectory, "Contributions");
    internal static string NormalizeEndpoint(string value)
    {
        if (value.Length > 2048 || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || string.IsNullOrWhiteSpace(uri.Host))
            throw new ArgumentException("Enter an HTTPS API endpoint without a username, query, or fragment.");
        return uri.AbsolutePath.TrimEnd('/').Length == 0 ? uri.GetLeftPart(UriPartial.Authority) + "/api/contributions/v1/catalog" : uri.AbsoluteUri;
    }
    internal static string AccountId(string token)
    {
        var match = Regex.Match(token, @"\Adkpc_([a-f0-9]{32})_[A-Za-z0-9_-]{43}\z", RegexOptions.CultureInvariant);
        // Preserve legacy account scopes and key-rotation history. Generic
        // bearer tokens have no account identifier, so isolate them by token.
        if (match.Success) return match.Groups[1].Value;
        if (token.Length is < 1 or > 4096 || !Regex.IsMatch(token, @"\A[A-Za-z0-9._~+/-]+=*\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("Enter a valid bearer API key without spaces or control characters.");
        return "token-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    }
    internal static void AtomicWrite(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static T? Read<T>(string path, long limit = 134217728)
    {
        if (!File.Exists(path)) return default;
        if (new FileInfo(path).Length > limit) throw new InvalidDataException("Contribution storage exceeds its supported size.");
        return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Json);
    }
    internal static void Save<T>(string path, T value) => AtomicWrite(path, JsonSerializer.SerializeToUtf8Bytes(value, Json));
    internal static void SaveCredential(string path, ContributionCredential value) => AtomicWrite(path,
        ContributionProtection.Protect(JsonSerializer.SerializeToUtf8Bytes(value, Json)));
    internal static ContributionCredential? ReadCredential(string path)
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 16384) throw new InvalidDataException("Contribution key storage is invalid.");
        return JsonSerializer.Deserialize<ContributionCredential>(ContributionProtection.Unprotect(File.ReadAllBytes(path)), Json);
    }
}

// DPAPI's default scope is the current Windows user. No machine-wide flag,
// plaintext fallback, team-profile field, or additional package is involved.
internal static class ContributionProtection
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { internal int Length; internal nint Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob input, string? description,
        ref Blob entropy, nint reserved, nint prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob input, nint description,
        ref Blob entropy, nint reserved, nint prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint value);
    internal static byte[] Protect(byte[] bytes) => Transform(bytes, true);
    internal static byte[] Unprotect(byte[] bytes) => Transform(bytes, false);
    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var entropyBytes = Encoding.UTF8.GetBytes("TeamBar.DkpContributions.v1");
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        var entropy = new Blob { Length = entropyBytes.Length, Data = Marshal.AllocHGlobal(entropyBytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length); Marshal.Copy(entropyBytes, 0, entropy.Data, entropyBytes.Length);
            bool success = protect ? CryptProtectData(ref input, null, ref entropy, 0, 0, 1, out output)
                : CryptUnprotectData(ref input, 0, ref entropy, 0, 0, 1, out output);
            if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), "The contribution key could not be secured for this Windows user.");
            var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            if (output.Data != 0) LocalFree(output.Data);
            Marshal.FreeHGlobal(input.Data); Marshal.FreeHGlobal(entropy.Data);
        }
    }
}
