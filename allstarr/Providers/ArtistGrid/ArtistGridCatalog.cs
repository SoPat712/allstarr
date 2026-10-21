using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Services.Common;

namespace allstarr.Core.Providers.ArtistGrid;

internal static class ArtistGridConstants
{
    public const string ProviderId = "artistgrid";
    public const string HttpClientName = "ArtistGrid";
    public const string ArtistsUri = "https://artists.artistgrid.cx/artists.csv";
    public const string TrackerApiHost = "trackerapi.artistgrid.cx";
    public const int MaximumArtistBodyBytes = 1 * 1024 * 1024;
    public const int MaximumTrackerBodyBytes = 8 * 1024 * 1024;
    public const int MaximumArtistRows = 4096;
    public const int MaximumTrackRows = 10_000;
    public const int MaximumTrackerCacheEntries = 16;
    public const long MaximumTrackerCacheBytes = 32L * 1024 * 1024;
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(15);
}

internal sealed record ArtistGridArtist(
    string Name,
    string Tracker,
    string? Credit,
    string? Best);

internal sealed record ArtistGridArtistIndex(IReadOnlyList<ArtistGridArtist> Artists);

internal sealed record ArtistGridSource(
    string CanonicalUrl,
    Uri Endpoint,
    string Kind,
    string SourceId)
{
    public string Hash => ArtistGridTrackId.HashSource(CanonicalUrl);

    public IReadOnlySet<string> AllowedRedirectHosts =>
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Endpoint.Host };
}

internal sealed record ArtistGridRawTrack(
    string Title,
    IReadOnlyList<string> Credits,
    IReadOnlyList<ArtistGridSource> Sources,
    TimeSpan? Duration,
    string? AlbumTitle,
    string? Quality,
    string? AvailableLength);

internal sealed record ArtistGridTracker(
    string Tracker,
    string Gid,
    string Name,
    IReadOnlyList<ArtistGridRawTrack> Tracks,
    string? ETag,
    long BodyBytes);

internal sealed record ArtistGridResolvedTrack(
    string Tracker,
    string Gid,
    ArtistGridArtist Artist,
    ArtistGridRawTrack Track,
    ArtistGridSource Source,
    string? ETag);

internal sealed class ArtistGridCachedTracker(
    ArtistGridTracker tracker,
    DateTimeOffset expiresAt,
    long lastUsed)
{
    public ArtistGridTracker Tracker { get; } = tracker;
    public DateTimeOffset ExpiresAt { get; set; } = expiresAt;
    public long LastUsed { get; set; } = lastUsed;
}

internal static class ArtistGridTrackId
{
    private const string Prefix = "ag1";

    public static string Create(string tracker, string gid, string sourceUrlHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tracker);
        ArgumentException.ThrowIfNullOrWhiteSpace(gid);
        if (sourceUrlHash.Length != 64 || sourceUrlHash.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("A SHA-256 source hash is required.", nameof(sourceUrlHash));

        return string.Join('.', Prefix, Encode(tracker), Encode(gid), sourceUrlHash.ToLowerInvariant());
    }

    public static bool TryParse(
        string? value,
        out string tracker,
        out string gid,
        out string sourceUrlHash)
    {
        tracker = string.Empty;
        gid = string.Empty;
        sourceUrlHash = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var pieces = value.Split('.', StringSplitOptions.None);
        if (pieces.Length != 4 || !pieces[0].Equals(Prefix, StringComparison.Ordinal) ||
            pieces[1].Length == 0 || pieces[2].Length == 0 || pieces[3].Length != 64 ||
            pieces[3].Any(character => !Uri.IsHexDigit(character))) return false;

        try
        {
            tracker = Decode(pieces[1]);
            gid = Decode(pieces[2]);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            return false;
        }

        if (!TryNormalizeTracker(tracker, out tracker) || string.IsNullOrWhiteSpace(gid) || gid.Length > 200)
            return false;
        sourceUrlHash = pieces[3].ToLowerInvariant();
        return true;
    }

    public static string HashSource(string canonicalUrl) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(canonicalUrl))).ToLowerInvariant();

    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Decode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => throw new FormatException("Invalid base64url value.")
        };
        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }

    public static bool TryNormalizeTracker(string? value, out string tracker)
    {
        tracker = value?.Trim() ?? string.Empty;
        if (tracker.Length is 0 or > 200 || tracker.Contains('/') || tracker.Contains('\\') ||
            tracker.Contains(':') || tracker.Contains('?') || tracker.Contains('#') ||
            tracker.Any(character => !(char.IsLetterOrDigit(character) || character is '.' or '-' or '_' or '~')))
        {
            tracker = string.Empty;
            return false;
        }

        return true;
    }
}

public sealed class ArtistGridCatalog
{
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _fetchGate = new(1, 1);
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, ArtistGridCachedTracker> _trackerCache = new(StringComparer.Ordinal);
    private ArtistGridArtistIndex? _artistIndex;
    private string? _artistIndexETag;
    private DateTimeOffset _artistIndexExpiresAt;
    private long _cacheSequence;
    private long _trackerCacheBytes;

    public ArtistGridCatalog(HttpClient http, TimeProvider? clock = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _clock = clock ?? TimeProvider.System;
    }

    internal bool TryGetCachedArtistIndex(out ArtistGridArtistIndex? index)
    {
        lock (_cacheGate)
        {
            index = _artistIndex;
            return index != null;
        }
    }

    internal IReadOnlyList<(ArtistGridArtist Artist, ArtistGridTracker Tracker)> GetCachedTrackers()
    {
        lock (_cacheGate)
        {
            return _trackerCache.Values
                .OrderByDescending(item => item.LastUsed)
                .Select(item =>
                {
                    var artist = _artistIndex?.Artists.FirstOrDefault(candidate =>
                        candidate.Tracker.Equals(item.Tracker.Tracker, StringComparison.Ordinal));
                    return (artist ?? new ArtistGridArtist(
                        item.Tracker.Name,
                        item.Tracker.Tracker,
                        null,
                        null), item.Tracker);
                })
                .ToArray();
        }
    }

    internal async Task<ProviderOutcome<ArtistGridArtistIndex>> GetArtistIndexAsync(CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        lock (_cacheGate)
        {
            if (_artistIndex != null && now < _artistIndexExpiresAt)
                return ProviderOutcome<ArtistGridArtistIndex>.Success(_artistIndex);
        }

        var gateAcquired = false;
        try
        {
            await _fetchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateAcquired = true;
            now = _clock.GetUtcNow();
            string? etag;
            lock (_cacheGate)
            {
                if (_artistIndex != null && now < _artistIndexExpiresAt)
                    return ProviderOutcome<ArtistGridArtistIndex>.Success(_artistIndex);
                etag = _artistIndexETag;
            }

            var fetched = await FetchAsync(new Uri(ArtistGridConstants.ArtistsUri), etag,
                ArtistGridConstants.MaximumArtistBodyBytes, cancellationToken).ConfigureAwait(false);
            if (!fetched.IsSuccess) return ProviderOutcome<ArtistGridArtistIndex>.Failure(fetched.Error!);
            if (fetched.Value!.NotModified)
            {
                lock (_cacheGate)
                {
                    if (_artistIndex == null)
                        return Failure<ArtistGridArtistIndex>(ProviderErrorKind.CapabilityUnavailable);
                    _artistIndexExpiresAt = now + ArtistGridConstants.CacheLifetime;
                    return ProviderOutcome<ArtistGridArtistIndex>.Success(_artistIndex);
                }
            }

            if (!TryParseArtistIndex(fetched.Value.Body!, out var parsed))
                return Failure<ArtistGridArtistIndex>(ProviderErrorKind.CapabilityUnavailable);

            lock (_cacheGate)
            {
                _artistIndex = parsed;
                _artistIndexETag = fetched.Value.ETag;
                _artistIndexExpiresAt = now + ArtistGridConstants.CacheLifetime;
            }
            return ProviderOutcome<ArtistGridArtistIndex>.Success(parsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<ArtistGridArtistIndex>(ProviderErrorKind.Canceled);
        }
        catch (HttpRequestException)
        {
            return Failure<ArtistGridArtistIndex>(ProviderErrorKind.TransientFailure);
        }
        catch (OperationCanceledException)
        {
            return Failure<ArtistGridArtistIndex>(ProviderErrorKind.TransientFailure);
        }
        catch
        {
            return Failure<ArtistGridArtistIndex>(ProviderErrorKind.TransientFailure);
        }
        finally
        {
            if (gateAcquired) _fetchGate.Release();
        }
    }

    internal async Task<ProviderOutcome<ArtistGridTracker>> GetTrackerAsync(
        string tracker,
        string? gid,
        bool allowNetwork,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        if (!ArtistGridTrackId.TryNormalizeTracker(tracker, out tracker) || gid?.Length > 200)
            return Failure<ArtistGridTracker>(ProviderErrorKind.NotFound);

        var key = CacheKey(tracker, gid);
        var now = _clock.GetUtcNow();
        lock (_cacheGate)
        {
            if (_trackerCache.TryGetValue(key, out var cached))
            {
                Touch(cached);
                if (!allowNetwork || !forceRefresh && now < cached.ExpiresAt)
                    return ProviderOutcome<ArtistGridTracker>.Success(cached.Tracker);
            }
        }

        if (!allowNetwork)
            return Failure<ArtistGridTracker>(ProviderErrorKind.NotFound);

        var gateAcquired = false;
        try
        {
            await _fetchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateAcquired = true;
            now = _clock.GetUtcNow();
            string? etag;
            ArtistGridCachedTracker? cached;
            lock (_cacheGate)
            {
                _trackerCache.TryGetValue(key, out cached);
                if (cached != null) Touch(cached);
                if (cached != null && !forceRefresh && now < cached.ExpiresAt)
                    return ProviderOutcome<ArtistGridTracker>.Success(cached.Tracker);
                etag = cached?.Tracker.ETag;
            }

            var uri = TrackerUri(tracker, gid);
            var fetched = await FetchAsync(uri, etag, ArtistGridConstants.MaximumTrackerBodyBytes,
                cancellationToken).ConfigureAwait(false);
            if (!fetched.IsSuccess) return ProviderOutcome<ArtistGridTracker>.Failure(fetched.Error!);
            if (fetched.Value!.NotModified)
            {
                lock (_cacheGate)
                {
                    if (cached == null)
                        return Failure<ArtistGridTracker>(ProviderErrorKind.CapabilityUnavailable);
                    cached.ExpiresAt = now + ArtistGridConstants.CacheLifetime;
                    Touch(cached);
                    return ProviderOutcome<ArtistGridTracker>.Success(cached.Tracker);
                }
            }

            if (!TryParseTracker(fetched.Value.Body!, tracker, gid, fetched.Value.ETag, out var parsed))
                return Failure<ArtistGridTracker>(ProviderErrorKind.CapabilityUnavailable);
            StoreTracker(key, parsed, now + ArtistGridConstants.CacheLifetime);
            return ProviderOutcome<ArtistGridTracker>.Success(parsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<ArtistGridTracker>(ProviderErrorKind.Canceled);
        }
        catch (HttpRequestException)
        {
            return Failure<ArtistGridTracker>(ProviderErrorKind.TransientFailure);
        }
        catch (OperationCanceledException)
        {
            return Failure<ArtistGridTracker>(ProviderErrorKind.TransientFailure);
        }
        catch
        {
            return Failure<ArtistGridTracker>(ProviderErrorKind.TransientFailure);
        }
        finally
        {
            if (gateAcquired) _fetchGate.Release();
        }
    }

    internal async Task<ProviderOutcome<ArtistGridResolvedTrack>> ResolveTrackAsync(
        string tracker,
        string gid,
        string sourceHash,
        CancellationToken cancellationToken)
    {
        var trackerResult = await GetTrackerAsync(tracker, gid, allowNetwork: true, forceRefresh: true,
            cancellationToken).ConfigureAwait(false);
        if (!trackerResult.IsSuccess)
            return ProviderOutcome<ArtistGridResolvedTrack>.Failure(trackerResult.Error!);

        ArtistGridArtist? artist = null;
        lock (_cacheGate)
        {
            artist = _artistIndex?.Artists.FirstOrDefault(item =>
                item.Tracker.Equals(tracker, StringComparison.Ordinal));
        }
        var trackerValue = trackerResult.RequireValue();
        artist ??= new ArtistGridArtist(trackerValue.Name, tracker, null, null);

        var matches = trackerValue.Tracks
            .Select(track => (Track: track, Source: SelectSource(track.Sources, sourceHash)))
            .Where(item => item.Source != null && IsUsableTrack(item.Track))
            .OrderBy(item => item.Source!.CanonicalUrl, StringComparer.Ordinal)
            .ThenBy(item => NormalizeForSearch(item.Track.Title), StringComparer.Ordinal)
            .ThenBy(item => NormalizeForSearch(item.Track.AlbumTitle ?? string.Empty), StringComparer.Ordinal)
            .ToArray();
        var selected = matches.FirstOrDefault();
        if (selected.Source == null)
            return Failure<ArtistGridResolvedTrack>(ProviderErrorKind.NotFound);

        return ProviderOutcome<ArtistGridResolvedTrack>.Success(new(
            tracker,
            gid,
            artist,
            selected.Track,
            selected.Source,
            trackerValue.ETag));
    }

    private async Task<ProviderOutcome<ArtistGridFetch>> FetchAsync(
        Uri uri,
        string? etag,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (!OutboundRequestGuard.TryCreateSafeHttpUri(uri.AbsoluteUri, out var safeUri, out _))
            return Failure<ArtistGridFetch>(ProviderErrorKind.CapabilityUnavailable);

        using var request = new HttpRequestMessage(HttpMethod.Get, safeUri);
        request.Headers.Accept.ParseAdd("application/json, text/csv;q=0.9, */*;q=0.1");
        if (!string.IsNullOrWhiteSpace(etag))
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<ArtistGridFetch>(ProviderErrorKind.Canceled);
        }
        catch (HttpRequestException)
        {
            return Failure<ArtistGridFetch>(ProviderErrorKind.TransientFailure);
        }

        using (response)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
                return ProviderOutcome<ArtistGridFetch>.Success(new(null, response.Headers.ETag?.Tag ?? etag, true));
            if ((int)response.StatusCode is >= 300 and < 400 || response.Headers.Location != null)
                return Failure<ArtistGridFetch>(ProviderErrorKind.CapabilityUnavailable);
            var statusError = ErrorFor(response.StatusCode);
            if (statusError != null)
                return ProviderOutcome<ArtistGridFetch>.Failure(statusError);

            if (response.Content.Headers.ContentLength is > ArtistGridConstants.MaximumTrackerBodyBytes)
                return Failure<ArtistGridFetch>(ProviderErrorKind.CapabilityUnavailable);
            if (response.Content.Headers.ContentLength is { } contentLength && contentLength > maximumBytes)
                return Failure<ArtistGridFetch>(ProviderErrorKind.CapabilityUnavailable);

            var body = await ReadBoundedAsync(response.Content, maximumBytes, cancellationToken)
                .ConfigureAwait(false);
            if (body == null)
                return Failure<ArtistGridFetch>(ProviderErrorKind.CapabilityUnavailable);
            return ProviderOutcome<ArtistGridFetch>.Success(new(body, response.Headers.ETag?.Tag, false));
        }
    }

    private static async Task<byte[]?> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (destination.Length + read > maximumBytes) return null;
            destination.Write(buffer, 0, read);
        }
        return destination.ToArray();
    }

    private static ProviderError? ErrorFor(System.Net.HttpStatusCode status) => status switch
    {
        System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone => new(ProviderErrorKind.NotFound),
        System.Net.HttpStatusCode.Unauthorized => new(ProviderErrorKind.Unauthorized),
        System.Net.HttpStatusCode.Forbidden => new(ProviderErrorKind.Forbidden),
        System.Net.HttpStatusCode.TooManyRequests => new(ProviderErrorKind.RateLimited, TimeSpan.FromSeconds(30)),
        System.Net.HttpStatusCode.RequestTimeout or >= System.Net.HttpStatusCode.InternalServerError => new(ProviderErrorKind.TransientFailure),
        _ when (int)status >= 400 => new(ProviderErrorKind.PermanentFailure),
        _ => null
    };

    private static Uri TrackerUri(string tracker, string? gid)
    {
        var trackerSegment = Uri.EscapeDataString(tracker);
        return gid == null
            ? new Uri($"https://{ArtistGridConstants.TrackerApiHost}/sh/{trackerSegment}/")
            : new Uri($"https://{ArtistGridConstants.TrackerApiHost}/sh/{trackerSegment}/gid/{Uri.EscapeDataString(gid)}");
    }

    private static string CacheKey(string tracker, string? gid) => tracker + "\u001f" + (gid ?? string.Empty);

    private void StoreTracker(string key, ArtistGridTracker tracker, DateTimeOffset expiresAt)
    {
        lock (_cacheGate)
        {
            if (_trackerCache.Remove(key, out var old)) _trackerCacheBytes -= old.Tracker.BodyBytes;
            while (_trackerCache.Count >= ArtistGridConstants.MaximumTrackerCacheEntries ||
                   _trackerCacheBytes + tracker.BodyBytes > ArtistGridConstants.MaximumTrackerCacheBytes)
            {
                var oldest = _trackerCache.OrderBy(item => item.Value.LastUsed).FirstOrDefault();
                if (oldest.Key == null) break;
                _trackerCache.Remove(oldest.Key);
                _trackerCacheBytes -= oldest.Value.Tracker.BodyBytes;
            }
            var cached = new ArtistGridCachedTracker(tracker, expiresAt, ++_cacheSequence);
            _trackerCache[key] = cached;
            _trackerCacheBytes += tracker.BodyBytes;
        }
    }

    private void Touch(ArtistGridCachedTracker cached)
    {
        cached.LastUsed = ++_cacheSequence;
    }

    private static bool TryParseArtistIndex(byte[] body, out ArtistGridArtistIndex index)
    {
        index = new ArtistGridArtistIndex([]);
        var text = Encoding.UTF8.GetString(body).TrimStart('\uFEFF');
        if (!TryParseCsv(text, out var rows) || rows.Count == 0) return false;
        var header = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var offset = 0; offset < rows[0].Count; offset++)
        {
            var name = rows[0][offset].Trim().ToLowerInvariant();
            if (name.Length > 0 && !header.TryAdd(name, offset)) return false;
        }
        if (!header.TryGetValue("name", out var nameIndex) || !header.TryGetValue("url", out var urlIndex))
            return false;
        var creditIndex = header.TryGetValue("credit", out var parsedCreditIndex)
            ? parsedCreditIndex : -1;
        var bestIndex = header.TryGetValue("best", out var parsedBestIndex)
            ? parsedBestIndex : -1;
        var artists = new List<ArtistGridArtist>();
        foreach (var row in rows.Skip(1))
        {
            if (artists.Count >= ArtistGridConstants.MaximumArtistRows) return false;
            var name = Field(row, nameIndex)?.Trim();
            var tracker = Field(row, urlIndex)?.Trim();
            if (!IsSafeMetadataText(name, 500) ||
                !ArtistGridTrackId.TryNormalizeTracker(tracker, out tracker)) continue;
            var creditValue = Field(row, creditIndex)?.Trim();
            if (!IsSafeMetadataText(creditValue, 500)) creditValue = null;
            artists.Add(new(
                name!,
                tracker,
                creditValue,
                Field(row, bestIndex)?.Trim() is { Length: > 0 } best ? best : null));
        }
        index = new ArtistGridArtistIndex(artists
            .OrderBy(item => NormalizeForSearch(item.Name), StringComparer.Ordinal)
            .ThenBy(item => item.Tracker, StringComparer.Ordinal)
            .ToArray());
        return true;
    }

    private static bool TryParseTracker(
        byte[] body,
        string tracker,
        string? requestedGid,
        string? etag,
        out ArtistGridTracker parsed)
    {
        parsed = new ArtistGridTracker(tracker, requestedGid ?? string.Empty, tracker, [], etag, body.LongLength);
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var tab = Property(root, "tab");
            var gid = String(tab, "gid") ?? requestedGid;
            if (string.IsNullOrWhiteSpace(gid) || requestedGid != null && !gid.Equals(requestedGid, StringComparison.Ordinal))
                return false;
            var name = String(root, "name") ?? String(tab, "name") ?? tracker;
            var tracks = new List<ArtistGridRawTrack>();
            var eras = Property(root, "eras");
            if (eras.ValueKind == JsonValueKind.Array)
            {
                foreach (var era in eras.EnumerateArray())
                {
                    var album = String(era, "name");
                    var eraTracks = Property(era, "tracks");
                    if (eraTracks.ValueKind != JsonValueKind.Array) continue;
                    foreach (var value in eraTracks.EnumerateArray())
                    {
                        if (TryParseTrack(value, album, out var track)) tracks.Add(track);
                        if (tracks.Count > ArtistGridConstants.MaximumTrackRows) return false;
                    }
                }
            }
            var flatTracks = Property(root, "tracks");
            if (flatTracks.ValueKind == JsonValueKind.Array)
            {
                foreach (var value in flatTracks.EnumerateArray())
                {
                    if (TryParseTrack(value, String(tab, "name"), out var track)) tracks.Add(track);
                    if (tracks.Count > ArtistGridConstants.MaximumTrackRows) return false;
                }
            }
            parsed = new ArtistGridTracker(tracker, gid, name, tracks, etag, body.LongLength);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseTrack(JsonElement value, string? album, out ArtistGridRawTrack track)
    {
        track = new ArtistGridRawTrack(string.Empty, [], [], null, album, null, null);
        if (value.ValueKind != JsonValueKind.Object) return false;
        var name = Property(value, "name");
        var title = name.ValueKind == JsonValueKind.Object
            ? String(name, "raw") ?? String(name, "title")
            : name.ValueKind == JsonValueKind.String ? name.GetString() : null;
        if (string.IsNullOrWhiteSpace(title)) return false;

        var credits = new List<string>();
        var creditValues = name.ValueKind == JsonValueKind.Object ? Property(name, "credits") : default;
        if (creditValues.ValueKind == JsonValueKind.Array)
        {
            foreach (var credit in creditValues.EnumerateArray())
            {
                var creditName = credit.ValueKind == JsonValueKind.String
                    ? credit.GetString()
                    : credit.ValueKind == JsonValueKind.Object
                        ? String(credit, "name") ?? String(credit, "raw") ?? String(credit, "title")
                        : null;
                if (!string.IsNullOrWhiteSpace(creditName) && !credits.Contains(creditName.Trim(), StringComparer.Ordinal))
                    credits.Add(creditName.Trim());
            }
        }

        var sources = new List<ArtistGridSource>();
        var links = Property(value, "links");
        if (links.ValueKind == JsonValueKind.Array)
        {
            foreach (var link in links.EnumerateArray())
            {
                var rawUrl = String(link, "url");
                if (TryParseSource(rawUrl, out var source) &&
                    !sources.Any(item => item.CanonicalUrl.Equals(source.CanonicalUrl, StringComparison.Ordinal)))
                    sources.Add(source);
            }
        }

        track = new ArtistGridRawTrack(
            string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)),
            credits,
            sources.OrderBy(item => item.CanonicalUrl, StringComparer.Ordinal).ToArray(),
            ParseDuration(String(value, "track_length")),
            album,
            String(value, "quality"),
            String(value, "available_length"));
        return true;
    }

    private static bool TryParseSource(string? rawUrl, out ArtistGridSource source)
    {
        source = null!;
        if (string.IsNullOrWhiteSpace(rawUrl) ||
            !Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Port != 443 || rawUrl.Length > 4096 ||
            !OutboundRequestGuard.TryCreateSafeHttpUri(uri.AbsoluteUri, out var safeUri, out _)) return false;

        if (safeUri!.HostNameType != UriHostNameType.Dns || !safeUri.Host.Contains('.')) return false;
        if (TryParseHostedSource(safeUri, out source)) return true;
        var extension = Path.GetExtension(safeUri.AbsolutePath).ToLowerInvariant();
        if (extension is ".mp3" or ".m4a" or ".flac" or ".wav" or ".ogg" or ".aac")
        {
            source = new ArtistGridSource(safeUri.AbsoluteUri, safeUri, "direct", safeUri.AbsolutePath);
            return true;
        }
        return false;
    }

    private static bool TryParseHostedSource(Uri uri, out ArtistGridSource source)
    {
        source = null!;
        if (uri.Query.Length != 0 && !uri.Host.Equals("drive.google.com", StringComparison.OrdinalIgnoreCase)) return false;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString).ToArray();
        if (segments.Any(segment => string.IsNullOrWhiteSpace(segment) || !IsSafeSourceSegment(segment))) return false;
        var host = uri.Host.ToLowerInvariant();
        string kind;
        string sourceId;
        Uri endpoint;
        if (host == "pillows.su" && segments is ["f", var pillowsId])
        {
            kind = "pillows";
            sourceId = pillowsId;
            endpoint = new Uri($"https://api.pillows.su/api/download/{Uri.EscapeDataString(sourceId)}");
        }
        else if (host == "pixeldrain.com" && segments is [var pixeldrainMode, var pixeldrainId] &&
                 pixeldrainMode is "u" or "d")
        {
            kind = "pixeldrain";
            sourceId = pixeldrainId;
            endpoint = new Uri($"https://fuck-unvaulted.artistgrid.cx/{Uri.EscapeDataString(sourceId)}");
        }
        else if (host == "drive.google.com" && segments.Length is 3 or 4 &&
                 segments[0] == "file" && segments[1] == "d" &&
                 (segments.Length == 3 || segments[3] == "view"))
        {
            kind = "drive";
            sourceId = segments[2];
            endpoint = new Uri($"https://fuck-unvaulted.artistgrid.cx/gd/{Uri.EscapeDataString(sourceId)}");
        }
        else if (host == "music.froste.lol" && segments.Length is 2 or 3 &&
                 segments[0] == "song" && (segments.Length == 2 || segments[2] == "download") &&
                 segments[1].Length <= 128 && segments[1].All(character => character is >= 'a' and <= 'f' or >= '0' and <= '9'))
        {
            kind = "froste";
            sourceId = segments[1];
            endpoint = new Uri($"https://music.froste.lol/song/{sourceId}/download");
        }
        else return false;

        source = new ArtistGridSource(uri.AbsoluteUri, endpoint, kind, sourceId);
        return true;
    }

    private static bool IsSafeSourceSegment(string value) => value.Length <= 300 &&
        value.All(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_' or '~');

    private static bool IsUsableTrack(ArtistGridRawTrack track) =>
        track.Sources.Count > 0 && (string.IsNullOrWhiteSpace(track.AvailableLength) ||
            track.AvailableLength.Equals("full", StringComparison.OrdinalIgnoreCase));

    private static bool IsSafeMetadataText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        value.Equals(value.Trim(), StringComparison.Ordinal) && !value.Any(char.IsControl);

    private static ArtistGridSource? SelectSource(IReadOnlyList<ArtistGridSource> sources, string? hash = null) =>
        (hash == null
            ? sources.OrderBy(item => item.CanonicalUrl, StringComparer.Ordinal)
            : sources.Where(item => item.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.CanonicalUrl, StringComparer.Ordinal)).FirstOrDefault();

    private static TimeSpan? ParseDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Trim().Split(':');
        if (parts.Length is < 2 or > 3 || parts.Any(part => !int.TryParse(part, NumberStyles.None,
                CultureInfo.InvariantCulture, out _))) return null;
        if (!int.TryParse(parts[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds is < 0 or > 59 ||
            !int.TryParse(parts[^2], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) || minutes is < 0 or > 59)
            return null;
        var hours = 0;
        if (parts.Length == 3 && (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hours) || hours < 0))
            return null;
        return new TimeSpan(hours, minutes, seconds);
    }

    private static bool TryParseCsv(string text, out List<List<string>> rows)
    {
        rows = [];
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var atFieldStart = true;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (quoted)
            {
                if (character == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else quoted = false;
                }
                else field.Append(character);
                continue;
            }

            if (character == '"' && atFieldStart)
            {
                quoted = true;
                atFieldStart = false;
            }
            else if (character == ',')
            {
                row.Add(field.ToString());
                field.Clear();
                atFieldStart = true;
            }
            else if (character is '\r' or '\n')
            {
                if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                row.Add(field.ToString());
                field.Clear();
                if (row.Any(value => value.Length > 0)) rows.Add(row);
                row = [];
                atFieldStart = true;
            }
            else
            {
                field.Append(character);
                atFieldStart = false;
            }
        }
        if (quoted) return false;
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            if (row.Any(value => value.Length > 0)) rows.Add(row);
        }
        return true;
    }

    private static string? Field(IReadOnlyList<string> row, int index) =>
        index >= 0 && index < row.Count ? row[index] : null;

    private static JsonElement Property(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var result)
            ? result
            : default;

    private static string? String(JsonElement value, string property)
    {
        var result = Property(value, property);
        return result.ValueKind == JsonValueKind.String ? result.GetString() : null;
    }

    internal static string NormalizeForSearch(string value) =>
        new string(value.Normalize(NormalizationForm.FormD)
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            .ToArray())
        .ToLowerInvariant()
        .Replace("’", "'", StringComparison.Ordinal);

    private static ProviderOutcome<T> Failure<T>(ProviderErrorKind kind) =>
        ProviderOutcome<T>.Failure(new ProviderError(kind));

    private sealed record ArtistGridFetch(byte[]? Body, string? ETag, bool NotModified);
}
