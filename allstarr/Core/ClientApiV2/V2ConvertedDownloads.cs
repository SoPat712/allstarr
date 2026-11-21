using System.Collections.Concurrent;
using allstarr.Core.Playback;

namespace allstarr.Core.ClientApiV2;

/// <summary>
/// Converted copies kept as files, for downloads. A converted stream has no length and cannot resume, and a download
/// over a poor connection has to. So a download's conversion is made once into a file, and that file is served with a
/// length and byte ranges. A copy belongs to no listener: it is reached only through a stream link, which resolve gives
/// to listeners allowed the song.
/// </summary>
public sealed class V2ConvertedDownloads
{
    public const string HttpClientName = "ConvertedDownloadSource";
    public const string DirectoryKey = "Downloads:ConvertedDirectory";
    private const string Extension = ".audio", PartExtension = ".part";
    // A copy read this recently may be a download still in progress, or one about to resume.
    private static readonly TimeSpan InUseFor = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MakeLimit = TimeSpan.FromMinutes(5);
    private const long KeepFree = 512L * 1024 * 1024;
    private readonly string directory;
    private readonly long maximumBytes;
    private readonly IHttpClientFactory clients;
    private readonly TimeProvider clock;
    private readonly ILogger<V2ConvertedDownloads>? logger;
    private readonly ConcurrentDictionary<string, Lazy<Task<Made>>> making = new(StringComparer.Ordinal);
    private readonly object sweeping = new();
    // Listening converts too, and a full set of conversions turns a new one away at once. A playlist being downloaded
    // would keep every conversion busy and nothing could start playing, so copies never take the last one.
    private readonly Func<int> conversions;
    private readonly object turns = new();
    private int makingNow;
    /// <summary>How long a copy waits for its turn before its download is told to come back.</summary>
    internal TimeSpan TurnLimit { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>The copy, or the status its source answered with and how long to wait before asking again.</summary>
    public sealed record Made(FileInfo? File, int Status, TimeSpan? RetryAfter);

    public V2ConvertedDownloads(IConfiguration configuration, IHttpClientFactory clients, TimeProvider clock,
        ILogger<V2ConvertedDownloads>? logger = null, MediaCapacityLimits? limits = null)
        : this(configuration[DirectoryKey] is { Length: > 0 } configured ? configured : Path.Combine(Path.GetTempPath(), "allstarr-converted-downloads"),
            Math.Clamp(configuration.GetValue("Downloads:ConvertedMaximumMegabytes", 1024L), 64, 1024 * 1024) * 1024 * 1024, clients, clock, logger,
            limits == null ? null : () => limits.Conversions)
    {
    }

    internal V2ConvertedDownloads(string directory, long maximumBytes, IHttpClientFactory clients, TimeProvider clock,
        ILogger<V2ConvertedDownloads>? logger = null, Func<int>? conversions = null)
    {
        this.directory = directory; this.maximumBytes = maximumBytes; this.clients = clients; this.clock = clock; this.logger = logger;
        this.conversions = conversions ?? (() => 2);
    }

    /// <summary>The finished copy for a key, marked as read just now, or null when none is held.</summary>
    public FileInfo? Find(string key)
    {
        if (!ValidKey(key)) return null;
        var file = new FileInfo(Path.Combine(directory, key + Extension));
        if (!file.Exists || file.Length == 0) return null;
        // The read time says which copies are in use; the written time stays, since a resumed download checks it.
        try { File.SetLastAccessTimeUtc(file.FullName, clock.GetUtcNow().UtcDateTime); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return file;
    }

    /// <summary>A value that changes whenever the copy's bytes could have, for a resumed download to check.</summary>
    public static string EntityTag(string key, FileInfo file) => $"\"{key[..Math.Min(16, key.Length)]}-{file.Length:x}-{file.LastWriteTimeUtc.Ticks:x}\"";

    /// <summary>
    /// Reads the converted stream at <paramref name="source"/> into a copy. Requests for the same key share one read,
    /// and the read finishes even when the listener who started it has gone, so a retry finds the copy.
    /// </summary>
    public async Task<Made> MakeAsync(string key, Uri source, CancellationToken token)
    {
        if (!ValidKey(key)) return new(null, StatusCodes.Status404NotFound, null);
        if (Find(key) is { } held) return new(held, StatusCodes.Status200OK, null);
        var work = making.GetOrAdd(key, _ => new Lazy<Task<Made>>(() => Task.Run(() => MakeOnceAsync(key, source))));
        try { return await work.Value.WaitAsync(token); }
        finally
        {
            if (work.Value.IsCompleted) making.TryRemove(new KeyValuePair<string, Lazy<Task<Made>>>(key, work));
        }
    }

    private async Task<Made> MakeOnceAsync(string key, Uri source)
    {
        if (!await TakeTurnAsync()) return new(null, StatusCodes.Status503ServiceUnavailable, TimeSpan.FromSeconds(5));
        var part = Path.Combine(directory, $"{key}.{Guid.NewGuid():N}{PartExtension}");
        try
        {
            Directory.CreateDirectory(directory);
            if (Free() < KeepFree)
            {
                logger?.LogWarning("A converted download was not made: the disk has under {KeepFreeMb} MB free", KeepFree / 1024 / 1024);
                return new(null, StatusCodes.Status503ServiceUnavailable, TimeSpan.FromMinutes(1));
            }
            using var stop = new CancellationTokenSource(MakeLimit, clock);
            using var request = new HttpRequestMessage(HttpMethod.Get, source);
            using var response = await clients.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop.Token);
            if (!response.IsSuccessStatusCode)
                return new(null, (int)response.StatusCode, response.Headers.RetryAfter?.Delta ??
                    (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable ? TimeSpan.FromSeconds(5) : null));
            await using (var file = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
                await response.Content.CopyToAsync(file, stop.Token);
            if (new FileInfo(part).Length == 0) return new(null, StatusCodes.Status503ServiceUnavailable, TimeSpan.FromSeconds(5));
            var final = Path.Combine(directory, key + Extension);
            File.Move(part, final, overwrite: true);
            Sweep(key);
            return new(new FileInfo(final), StatusCodes.Status200OK, null);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            // A conversion that stopped part-way leaves a short file; it must never be served as the whole song.
            logger?.LogWarning("A converted download could not be made ({ErrorType})", error.GetType().Name);
            return new(null, StatusCodes.Status503ServiceUnavailable, TimeSpan.FromSeconds(10));
        }
        finally
        {
            lock (turns) makingNow--;
            try { File.Delete(part); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    // Copies wait for one another rather than being turned away: a download of many songs then runs as fast as its
    // share of the conversions allows, with no retries. Only a long queue sends a download away to come back.
    private async Task<bool> TakeTurnAsync()
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            lock (turns)
            {
                if (makingNow < Math.Max(1, conversions() - 1)) { makingNow++; return true; }
            }
            if (waited.Elapsed >= TurnLimit) return false;
            await Task.Delay(50);
        }
    }

    // Free space where the copies live; when it cannot be read, making a copy is not held back by it.
    private long Free()
    {
        try
        {
            var full = Path.GetFullPath(directory);
            var drive = DriveInfo.GetDrives().Where(item => full.StartsWith(item.Name, StringComparison.Ordinal))
                .OrderByDescending(item => item.Name.Length).FirstOrDefault();
            return drive is { IsReady: true } ? drive.AvailableFreeSpace : long.MaxValue;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return long.MaxValue; }
    }

    /// <summary>Removes the copies read longest ago while the folder is over its size, never one that may be in use.</summary>
    private void Sweep(string justMade)
    {
        lock (sweeping)
        {
            try
            {
                var now = clock.GetUtcNow().UtcDateTime;
                var folder = new DirectoryInfo(directory);
                foreach (var stale in folder.EnumerateFiles("*" + PartExtension).Where(file => now - file.LastWriteTimeUtc > MakeLimit + MakeLimit))
                    stale.Delete();
                var copies = folder.EnumerateFiles("*" + Extension).ToArray();
                var total = copies.Sum(file => file.Length);
                foreach (var copy in copies.OrderBy(file => file.LastAccessTimeUtc))
                {
                    if (total <= maximumBytes) break;
                    if (copy.Name == justMade + Extension || now - copy.LastAccessTimeUtc < InUseFor) continue;
                    total -= copy.Length;
                    copy.Delete();
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { logger?.LogDebug("Converted downloads could not be tidied ({ErrorType})", error.GetType().Name); }
        }
    }

    private static bool ValidKey(string? key) => key is { Length: >= 16 and <= 128 } && key.All(char.IsAsciiHexDigit);
}
