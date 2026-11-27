using System.Net.Http;
using System.Security.Cryptography;

namespace TeamBar;

internal static class ReleaseInstallerFile
{
    internal static async Task WriteVerified(HttpContent content, Stream output, long bytes, string sha256, CancellationToken token)
    {
        if (content.Headers.ContentLength is { } length && length != bytes)
            throw new InvalidDataException("The installer size does not match its release metadata.");
        await using var input = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920];
        long written = 0;
        while (true)
        {
            int count = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) break;
            written += count;
            if (written > bytes) throw new InvalidDataException("The download exceeded its expected size.");
            hash.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
        }
        if (written != bytes || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(sha256)))
            throw new InvalidDataException("The installer download is incomplete or its SHA-256 digest does not match.");
        await output.FlushAsync(token).ConfigureAwait(false);
    }

    internal static FileStream OpenVerified(string path, long bytes, string sha256)
    {
        var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (locked.Length != bytes || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(locked), Convert.FromHexString(sha256)))
                throw new InvalidDataException("The staged installer differs from the release. Download the update again.");
            return locked;
        }
        catch { locked.Dispose(); throw; }
    }
}
