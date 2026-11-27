using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TeamBar;

internal sealed class ContributionClient : IDisposable
{
    private readonly string directory;
    private readonly HttpClient http;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly SemaphoreSlim gate = new(1, 1), wake = new(0, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<CancellationTokenSource> epochs = [];
    private CancellationTokenSource epoch = new();
    private readonly Task initialized, sender;
    private Task? shutdown;
    private FileStream? lease;
    private ContributionCredential? credential;
    private ContributionQueue? queue;
    private ContributionSettings settings = new();
    private ContributionStatus status = new("Loading contribution settings…");
    private ContributionDelivery[] recent = [];
    private bool paused;
    private int generation, disposed;
    private long lastAttemptStarted;
    internal event Action? Changed;
    internal ContributionSettings Settings => Volatile.Read(ref settings);
    internal ContributionStatus Status => Volatile.Read(ref status);
    internal ContributionDelivery[] Recent => Volatile.Read(ref recent);
    internal Task Ready => initialized;
    internal Task Completion => shutdown ?? sender;
    internal TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    private string SettingsPath => Path.Combine(directory, "settings.json");
    private string KeyPath => Path.Combine(directory, "key.dpapi");

    internal ContributionClient() : this(ContributionStorage.DefaultDirectory, CreateHandler()) { }
    internal ContributionClient(string directory, HttpMessageHandler handler,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.directory = Path.GetFullPath(directory);
        this.delay = delay ?? Task.Delay;
        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        epochs.Add(epoch);
        initialized = Task.Run(Initialize);
        sender = Task.Run(SendLoop);
    }
    internal static HttpClientHandler CreateHandler() => new() { AllowAutoRedirect = false, UseCookies = false };
    private void Signal() { try { wake.Release(); } catch (SemaphoreFullException) { } }
    private void Publish(string message, bool sending = false, bool attention = false)
    {
        Volatile.Write(ref recent, queue?.Recent ?? []);
        Volatile.Write(ref status, new(message, queue?.Count ?? 0, sending, attention, queue?.LastUploadAtUtc));
        Notify();
    }
    private void Notify()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        foreach (Action callback in Changed?.GetInvocationList() ?? [])
            try { callback(); } catch (InvalidOperationException) { } catch (OperationCanceledException) { }
    }
    private void NewEpoch()
    {
        epoch.Cancel(); epoch = new(); epochs.Add(epoch); generation++; paused = false;
    }
    private void RequireLease()
    {
        if (lease is null) throw new IOException("Another collector window is managing contributions. Close it and reopen this window.");
    }
    private async Task Initialize()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(directory);
            lease = new FileStream(Path.Combine(directory, "sender.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var saved = ContributionStorage.Read<ContributionSettings>(SettingsPath, 16384) ?? new();
            if (saved.Endpoint is null || saved.AccountId is null) throw new InvalidDataException("Contribution settings are incomplete.");
            credential = ContributionStorage.ReadCredential(KeyPath);
            if (saved.Endpoint.Length > 0 && ContributionStorage.NormalizeEndpoint(saved.Endpoint) != saved.Endpoint)
                throw new InvalidDataException("The saved website address is invalid.");
            bool validKey = credential is not null && credential.Endpoint == saved.Endpoint && credential.AccountId == saved.AccountId
                && ContributionStorage.AccountId(credential.Token) == saved.AccountId;
            settings = saved with { HasApiKey = validKey, Data = saved.Data ?? new() };
            if (!validKey) credential = null;
            if (saved.Endpoint.Length > 0 && saved.AccountId.Length > 0)
                queue = new(directory, saved.Endpoint, saved.AccountId);
            paused = saved.Enabled && !validKey;
            Publish(paused ? "Enter a contribution API key for this website to continue." : saved.Enabled ? "Ready to contribute observed data." : "Contributions are off. Local collection is unchanged.", attention: paused);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { paused = true; Publish("Contribution storage is unavailable or another collector window is open. Close other collector windows and reopen this one.", attention: true); }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or System.ComponentModel.Win32Exception)
        { paused = true; credential = null; settings = settings with { HasApiKey = false }; Publish("Saved contribution settings or key could not be read. Re-enter the website and key, or clear the settings.", attention: true); }
        finally { gate.Release(); Signal(); }
    }
    internal Task ConfigureAsync(bool enabled, string endpoint, string? apiKey, CancellationToken cancellationToken = default, ContributionDataSelection? data = null) => Task.Run(async () =>
    {
        await initialized.ConfigureAwait(false);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await gate.WaitAsync(cancel.Token).ConfigureAwait(false);
        try
        {
            RequireLease();
            string normalized = string.IsNullOrWhiteSpace(endpoint) && !enabled ? "" : ContributionStorage.NormalizeEndpoint(endpoint);
            var nextCredential = credential;
            if (!string.IsNullOrWhiteSpace(apiKey)) nextCredential = new(normalized, ContributionStorage.AccountId(apiKey.Trim()), apiKey.Trim());
            else if (nextCredential is not null && normalized != nextCredential.Endpoint)
                throw new ArgumentException("Enter the API key again when changing websites.");
            if (enabled && nextCredential is null) throw new ArgumentException("Enter a contribution API key before enabling uploads.");
            var selection = data ?? settings.Data;
            if (enabled && !selection.Any) throw new ArgumentException("Choose at least one data type to share, or turn sharing off.");
            var next = new ContributionSettings(enabled, normalized, nextCredential?.AccountId ?? "", nextCredential is not null) { Data = selection };
            var nextQueue = next.HasApiKey ? new ContributionQueue(directory, next.Endpoint, next.AccountId) : null;
            NewEpoch();
            // Endpoint/account are encrypted with the key as well, so an interrupted
            // pair of atomic saves can never send an old key to a new website.
            if (nextCredential is not null) ContributionStorage.SaveCredential(KeyPath, nextCredential);
            ContributionStorage.Save(SettingsPath, next);
            credential = nextCredential; settings = next; queue = nextQueue;
            Publish(enabled ? "Ready to contribute observed data." : "Contributions are off. Pending data stays on this computer.");
        }
        catch { paused = true; Publish("Contribution settings were not saved. Check the website, key, and local storage.", attention: true); throw; }
        finally { gate.Release(); Signal(); }
    }, cancellationToken);

    internal Task ClearAsync(CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        await initialized.ConfigureAwait(false);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await gate.WaitAsync(cancel.Token).ConfigureAwait(false);
        try
        {
            RequireLease(); NewEpoch();
            ContributionStorage.Save(SettingsPath, new ContributionSettings());
            if (File.Exists(KeyPath)) File.Delete(KeyPath);
            settings = new(); credential = null; queue = null;
            Publish("Contribution settings and saved key cleared. Uploads are off.");
        }
        catch { paused = true; Publish("The saved key could not be cleared. Uploads are paused; check local storage and try again.", attention: true); throw; }
        finally { gate.Release(); Signal(); }
    }, cancellationToken);

    internal Task Enqueue(IEnumerable<ContributionRecord> records, CancellationToken cancellationToken = default, ContributionSettings? expectedSettings = null)
    {
        var captured = records.ToArray();
        var expected = expectedSettings ?? Settings;
        return Task.Run(async () =>
        {
            await initialized.ConfigureAwait(false);
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            await gate.WaitAsync(cancel.Token).ConfigureAwait(false);
            try
            {
                RequireLease();
                if (!ReferenceEquals(expected, settings) || !settings.Enabled || credential is null || queue is null) return;
                int before = queue.Count;
                queue.Enqueue(captured.Where(record => settings.Data.Includes(record.Kind)));
                if (queue.Count != before)
                {
                    var current = Status;
                    Volatile.Write(ref status, current with { PendingCount = queue.Count,
                        Message = current.IsSending || current.NeedsAttention || queue.RetryNotBeforeUtc > DateTimeOffset.UtcNow
                            ? current.Message : $"{queue.Count} observations queued for contribution." });
                    Notify();
                }
            }
            catch { paused = true; Publish("Observed data could not be saved to the contribution queue. Check local storage and retry.", attention: true); throw; }
            finally { gate.Release(); Signal(); }
        }, cancellationToken);
    }
    internal void Retry()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        _ = Task.Run(async () =>
        {
            await initialized.ConfigureAwait(false);
            await gate.WaitAsync().ConfigureAwait(false);
            try { if (Volatile.Read(ref disposed) == 0 && lease is not null) { NewEpoch(); Publish("Retrying pending contributions…"); } }
            finally { gate.Release(); Signal(); }
        });
    }
    private async Task SendLoop()
    {
        await initialized.ConfigureAwait(false);
        try
        {
            while (true)
            {
                await wake.WaitAsync(lifetime.Token).ConfigureAwait(false);
                int failures = 0;
                while (!lifetime.IsCancellationRequested)
                {
                    ContributionBatch? batch;
                    ContributionCredential active;
                    CancellationToken epochToken;
                    int version;
                    TimeSpan throttle;
                    await gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
                    try
                    {
                        if (!settings.Enabled || credential is null || queue is null || paused || lease is null) break;
                        batch = queue.Next(settings.Data);
                        if (batch is null) { Publish(queue.Count > 0 ? "Unselected data types remain queued locally. They will not be shared unless selected again." : "Contribution queue is up to date."); break; }
                        active = credential; version = generation; epochToken = epoch.Token;
                        throttle = queue.RetryNotBeforeUtc is { } deadline ? deadline - DateTimeOffset.UtcNow : TimeSpan.Zero;
                        Publish(throttle > TimeSpan.Zero ? "Waiting for the website's rate limit before retrying…" : $"Sending {batch.Records.Length} observations…", sending: throttle <= TimeSpan.Zero);
                    }
                    catch (Exception error) when (error is InvalidDataException or ArgumentException or JsonException)
                    { paused = true; Publish("Pending observations could not be prepared. Clear or repair the local queue before retrying.", attention: true); break; }
                    finally { gate.Release(); }
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, epochToken);
                    try
                    {
                        if (throttle > TimeSpan.Zero) { await delay(throttle, attempt.Token).ConfigureAwait(false); continue; }
                        if (lastAttemptStarted != 0)
                        {
                            var spacing = TimeSpan.FromMilliseconds(1100) - System.Diagnostics.Stopwatch.GetElapsedTime(lastAttemptStarted);
                            if (spacing > TimeSpan.Zero) await delay(spacing, attempt.Token).ConfigureAwait(false);
                        }
                        lastAttemptStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                        var result = await Send(active, batch, attempt.Token).ConfigureAwait(false);
                        await gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
                        try
                        {
                            if (version != generation) continue;
                            if (result.Success)
                            {
                                queue!.Acknowledge(batch, result.Receipts); failures = 0;
                                Publish(queue.Count == 0 ? "Contributions received for website review." : $"Contribution received. {queue.Count} observations remain.");
                            }
                            else if (result.Stop) { paused = true; Publish(result.Message, attention: true); break; }
                            else
                            {
                                if (result.RateLimited) queue!.DeferUntil(DateTimeOffset.UtcNow + Backoff(failures + 1, result.RetryAfter));
                                Publish(result.Message);
                            }
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                        { paused = true; Publish("Contribution progress could not be saved locally. Retry when storage is available; pending data was kept.", attention: true); break; }
                        finally { gate.Release(); }
                        TimeSpan wait = result.Success ? TimeSpan.FromMilliseconds(1100) : result.RateLimited ? TimeSpan.Zero : Backoff(++failures);
                        await delay(wait, attempt.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (attempt.IsCancellationRequested) { }
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    private sealed record SendResult(bool Success, bool Stop, string Message, TimeSpan? RetryAfter = null, bool RateLimited = false, ContributionReceipt[]? Receipts = null);
    private async Task<SendResult> Send(ContributionCredential active, ContributionBatch batch, CancellationToken cancel)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(RequestTimeout);
            var requestCancellation = timeout.Token;
            using var request = new HttpRequestMessage(HttpMethod.Post, active.Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", active.Token);
            request.Content = new ByteArrayContent(batch.Body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestCancellation).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return new(false, true, "The API key was rejected or revoked. Enter a current contribution key in Settings.");
            if ((int)response.StatusCode is 400 or 413 or 415) return new(false, true, "The website rejected the contribution format or size. Update the collector or check the website API before retrying.");
            if ((int)response.StatusCode == 429)
            {
                var header = response.Headers.RetryAfter;
                TimeSpan? retry = header?.Delta ?? (header?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
                return new(false, false, "The website is busy with this account. Pending data will retry automatically.", retry, true);
            }
            if ((int)response.StatusCode >= 500) return new(false, false, "The website is temporarily unavailable. Pending data will retry automatically.");
            if (!response.IsSuccessStatusCode) return new(false, true, "The website did not accept this request. Check the HTTPS address and contribution API configuration.");
            if (response.Content.Headers.ContentType?.MediaType != "application/json") return new(false, true, "The website returned an unexpected receipt. Pending data was kept; verify the API address before retrying.");
            await using var stream = await response.Content.ReadAsStreamAsync(requestCancellation).ConfigureAwait(false);
            using var bytes = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(buffer, requestCancellation).ConfigureAwait(false)) > 0)
            { if (bytes.Length + count > 131072) return new(false, true, "The website receipt was too large. Pending data was kept."); await bytes.WriteAsync(buffer.AsMemory(0, count), requestCancellation).ConfigureAwait(false); }
            var responseBytes = bytes.ToArray();
            return ValidReceipt(responseBytes, batch) ? new(true, false, "Received", Receipts: ReadReceipts(responseBytes))
                : new(false, true, "The website returned an incomplete receipt. Pending data was kept; retry after checking the API.");
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        { return new(false, false, "The upload timed out. Pending data will retry automatically."); }
        catch (HttpRequestException) { return new(false, false, "The website could not be reached. Pending data will retry automatically."); }
        catch (IOException) { return new(false, false, "The connection was interrupted. Pending data will retry automatically."); }
    }
    internal static bool ValidReceipt(byte[] bytes, ContributionBatch batch)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 1) return false;
            int accepted = root.GetProperty("accepted").GetInt32(), duplicates = root.GetProperty("duplicates").GetInt32();
            if (accepted < 0 || duplicates < 0 || accepted + duplicates != batch.Records.Length) return false;
            var expected = batch.Records.Select(record => $"{record.Kind}:{record.EntityId}").ToHashSet(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            int duplicateRows = 0;
            foreach (var row in root.GetProperty("records").EnumerateArray())
            {
                string key = $"{row.GetProperty("kind").GetString()}:{row.GetProperty("entityId").GetInt32()}";
                string id = row.GetProperty("id").GetString() ?? "";
                if (!expected.Contains(key) || !seen.Add(key) || !ids.Add(id) || !System.Text.RegularExpressions.Regex.IsMatch(id, @"\A[a-f0-9]{32}\z")) return false;
                if (row.GetProperty("status").GetString() is not ("pending" or "approved" or "rejected")) return false;
                if (row.GetProperty("duplicate").GetBoolean()) duplicateRows++;
            }
            return seen.Count == expected.Count && duplicateRows == duplicates;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException) { return false; }
    }
    private static ContributionReceipt[] ReadReceipts(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.GetProperty("records").EnumerateArray().Select(row => new ContributionReceipt(
            row.GetProperty("kind").GetString()!, row.GetProperty("entityId").GetInt32(), row.GetProperty("id").GetString()!,
            row.GetProperty("status").GetString()!, row.GetProperty("duplicate").GetBoolean())).ToArray();
    }
    internal static TimeSpan Backoff(int failures, TimeSpan? retryAfter = null)
    {
        if (retryAfter is { } requested) return TimeSpan.FromSeconds(Math.Max(1.1, requested.TotalSeconds) + Random.Shared.NextDouble());
        double seconds = Math.Min(300, 2 * Math.Pow(2, Math.Min(failures, 8)));
        return TimeSpan.FromSeconds(Math.Clamp(seconds + Random.Shared.NextDouble() * Math.Max(0.2, seconds * 0.15), 1.1, 300));
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel(); epoch.Cancel(); Signal();
        shutdown = Task.Run(async () =>
        {
            try { await sender.ConfigureAwait(false); }
            finally
            {
                await gate.WaitAsync().ConfigureAwait(false);
                try { http.Dispose(); lease?.Dispose(); foreach (var source in epochs) source.Dispose(); }
                finally { gate.Release(); }
            }
        });
    }
}
