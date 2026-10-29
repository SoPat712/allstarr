using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Music;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Storage;
using allstarr.Services.Common;

namespace allstarr.Core.Providers.LastFm;

public sealed class LastFmPlaylistCapabilityAdapter(
    HttpClient http,
    IProviderAccountSecretAccessor secrets,
    IApplicationCache cache) : IProviderPlaylistCapability
{
    public const string StableProviderId = "lastfm";
    private static readonly string[] Periods = ["7day", "1month", "3month", "12month", "overall"];
    public string ProviderId => StableProviderId;
    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Playlist;

    public Task<ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>> GetUserPlaylistsAsync(
        ProviderExecutionContext context, ProviderUserPlaylistsRequest request)
    {
        if (request.SourceUserId != null)
            return Task.FromResult(ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Failure(new(ProviderErrorKind.Forbidden)));
        return ExecuteAsync(context, (username, _, _) =>
        {
            var offset = Offset(request.Page.Cursor);
            var selections = new[] { "loved" }.Concat(Periods.Select(period => $"top:{period}:50")).Append("similar").ToArray();
            var items = selections.Skip(offset).Take(request.Page.Limit).Select(id =>
                Summary(Id(id), username, "selection", null, null)).ToArray();
            var next = offset + items.Length < selections.Length ? Number(offset + items.Length) : null;
            return Task.FromResult(new ProviderPage<ProviderPlaylistSummary>(ProviderId, items, next, next != null));
        });
    }

    public Task<ProviderOutcome<ProviderPlaylistTrackPage>> GetPlaylistTracksAsync(
        ProviderExecutionContext context, ProviderPlaylistTracksRequest request) => ExecuteAsync(context, async (username, apiKey, ct) =>
    {
        context.RequireResourceOwner(request.PlaylistId, ProviderResourceKind.Playlist);
        var (kind, period, limit) = Selection(request.PlaylistId.Value);
        var offset = Offset(request.Page.Cursor);
        var account = context.Account!;
        var cacheScope = Hash($"{context.Actor.EffectiveUserId}:{context.Actor.BackendPrincipal}:{account.AccountId}:{account.Revision}:{account.SecretReferenceId}:{request.PlaylistId.Value}");
        var cacheKey = $"playlist:discovery:v3:lastfm:snapshot:{cacheScope}:{request.ExpectedRevision}";
        var tracks = offset > 0 && request.ExpectedRevision != null ? await cache.GetAsync<Track[]>(cacheKey) : null;
        if (tracks == null)
        {
            tracks = kind == "similar"
                ? await SimilarAsync(username, apiKey, ct)
                : await ReadUserTracksAsync(username, apiKey, kind == "loved" ? "user.getlovedtracks" : "user.gettoptracks",
                    period, limit, ct);
        }
        var revision = Hash(JsonSerializer.Serialize(tracks));
        if (request.ExpectedRevision != null && request.ExpectedRevision != revision)
            throw new SourceFailure(new(ProviderErrorKind.TransientFailure));
        await cache.SetAsync($"playlist:discovery:v3:lastfm:snapshot:{cacheScope}:{revision}", tracks, TimeSpan.FromMinutes(5));
        var summary = Summary(request.PlaylistId, username, revision, tracks.Length,
            tracks.Length == 0 ? "No tracks were returned by Last.fm for this selection." : null);
        var items = tracks.Skip(offset).Take(request.Page.Limit).Select((track, index) =>
        {
            var id = new ProviderExternalResourceId(ProviderId, ProviderResourceKind.Track, Hash(Key(track)));
            var metadata = new ProviderTrackMetadata(id, track.Title, [new(track.Artist)],
                providerIds: track.Mbid == null ? null : new Dictionary<string, string> { ["musicbrainz"] = track.Mbid });
            return new ProviderPlaylistTrack(offset + index, id, metadata: metadata);
        }).ToArray();
        var next = offset + items.Length < tracks.Length ? Number(offset + items.Length) : null;
        return new ProviderPlaylistTrackPage(summary, new(ProviderId, items, next, next != null, revision));
    });

    public Task<ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>> SearchPlaylistsAsync(
        ProviderExecutionContext context, ProviderPlaylistSearchRequest request) => Task.FromResult(
            ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Failure(new(ProviderErrorKind.NotSupported)));

    private async Task<ProviderOutcome<T>> ExecuteAsync<T>(ProviderExecutionContext context,
        Func<string, string, CancellationToken, Task<T>> operation)
    {
        var account = context.Account;
        if (context.ProviderId != ProviderId || account is not { Enabled: true, Scope: ProviderAccountScope.Personal } ||
            context.Actor.EffectiveUserId != account.OwnerUserId || account.SecretReferenceId == null)
            return ProviderOutcome<T>.Failure(ProviderError.LastFmNotConnected());
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        deadline.CancelAfter(context.Remaining(DateTimeOffset.UtcNow));
        try
        {
            return await secrets.UseAsync(account, async bytes =>
            {
                deadline.Token.ThrowIfCancellationRequested();
                using var document = JsonDocument.Parse(bytes);
                var secret = document.RootElement;
                var key = Text(secret, "apiKey");
                if (key == null) return ProviderOutcome<T>.Failure(ProviderError.LastFmApiKeyRequired());
                var username = Text(secret, "username");
                if (username == null || Text(secret, "sessionKey") == null)
                    return ProviderOutcome<T>.Failure(ProviderError.LastFmNotConnected());
                return ProviderOutcome<T>.Success(await operation(username, key, deadline.Token));
            }, deadline.Token);
        }
        catch (SourceFailure failure) { return ProviderOutcome<T>.Failure(failure.Error); }
        catch (OperationCanceledException)
        {
            return ProviderOutcome<T>.Failure(new(context.CancellationToken.IsCancellationRequested
                ? ProviderErrorKind.Canceled : ProviderErrorKind.TransientFailure));
        }
        catch (HttpRequestException) { return ProviderOutcome<T>.Failure(new(ProviderErrorKind.TransientFailure)); }
        catch (KeyNotFoundException) { return ProviderOutcome<T>.Failure(ProviderError.LastFmNotConnected()); }
        catch (UnauthorizedAccessException) { return ProviderOutcome<T>.Failure(ProviderError.LastFmNotConnected()); }
        catch (JsonException) { return ProviderOutcome<T>.Failure(ProviderError.CompatibilityContractChanged()); }
        catch (ArgumentException) { return ProviderOutcome<T>.Failure(new(ProviderErrorKind.PermanentFailure)); }
    }

    private async Task<Track[]> ReadUserTracksAsync(string username, string apiKey, string method,
        string? period, int maximum, CancellationToken ct)
    {
        var result = new List<Track>();
        var pageSize = Math.Min(maximum, 200);
        for (var page = 1; page <= maximum; page++)
        {
            using var document = await GetAsync(apiKey, method,
                $"&user={Uri.EscapeDataString(username)}&limit={pageSize}&page={page}" +
                (period == null ? "" : $"&period={period}"), ct);
            var container = Container(document.RootElement, method == "user.getlovedtracks" ? "lovedtracks" : "toptracks");
            var attributes = container.TryGetProperty("@attr", out var attr) ? attr : default;
            if (Text(attributes, "page") != Number(page))
                throw new JsonException("Last.fm paging did not advance.");
            var tracks = ParseTracks(container).Take(pageSize).ToArray();
            result.AddRange(tracks.Take(maximum - result.Count));
            if (result.Count == maximum) break;
            if (!int.TryParse(Text(attributes, "totalPages"), out var pages) || pages < 0)
                throw new JsonException("Last.fm paging is missing.");
            if (page >= pages) break;
            if (tracks.Length == 0)
                throw new JsonException("Last.fm paging did not advance.");
        }
        return result.ToArray();
    }

    private async Task<Track[]> SimilarAsync(string username, string apiKey, CancellationToken ct)
    {
        var seeds = await ReadUserTracksAsync(username, apiKey, "user.gettoptracks", "1month", 10, ct);
        var used = seeds.Select(Key).ToHashSet(StringComparer.Ordinal);
        var mbids = seeds.Where(track => track.Mbid != null).Select(track => track.Mbid!).ToHashSet(StringComparer.Ordinal);
        var lists = new List<Track[]>();
        foreach (var seed in seeds.DistinctBy(Key))
        {
            using var document = await GetAsync(apiKey, "track.getsimilar",
                $"&autocorrect=1&limit=50&artist={Uri.EscapeDataString(seed.Artist)}&track={Uri.EscapeDataString(seed.Title)}", ct);
            lists.Add(ParseTracks(Container(document.RootElement, "similartracks")).Take(50).ToArray());
        }
        var result = new List<Track>();
        var artists = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var positions = new int[lists.Count];
        for (var round = 0; round < 50 && result.Count < 50; round++)
        {
            var added = false;
            for (var index = 0; index < lists.Count && result.Count < 50; index++)
            {
                while (positions[index] < lists[index].Length)
                {
                    var track = lists[index][positions[index]++];
                    if (used.Contains(Key(track)) || track.Mbid != null && mbids.Contains(track.Mbid) ||
                        artists.GetValueOrDefault(track.Artist) >= 3) continue;
                    used.Add(Key(track));
                    if (track.Mbid != null) mbids.Add(track.Mbid);
                    artists[track.Artist] = artists.GetValueOrDefault(track.Artist) + 1;
                    result.Add(track);
                    added = true;
                    break;
                }
            }
            if (!added) break;
        }
        return result.ToArray();
    }

    private async Task<JsonDocument> GetAsync(string apiKey, string method, string query, CancellationToken ct)
    {
        using var response = await http.GetAsync(
            $"https://ws.audioscrobbler.com/2.0/?method={method}&format=json&api_key={Uri.EscapeDataString(apiKey)}{query}",
            HttpCompletionOption.ResponseHeadersRead, ct);
        var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30);
        if (!response.IsSuccessStatusCode)
            throw new SourceFailure(response.StatusCode switch
            {
                HttpStatusCode.TooManyRequests => new(ProviderErrorKind.RateLimited, retryAfter),
                HttpStatusCode.Forbidden => ProviderError.LastFmPrivateProfile(),
                HttpStatusCode.Unauthorized => new(ProviderErrorKind.AccountNeedsReauthentication),
                >= HttpStatusCode.InternalServerError => new(ProviderErrorKind.TransientFailure),
                _ => new(ProviderErrorKind.PermanentFailure)
            });
        const int maximumBytes = 1024 * 1024;
        if (response.Content.Headers.ContentLength > maximumBytes) throw new JsonException("Last.fm response exceeded its bound.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maximumBytes) throw new JsonException("Last.fm response exceeded its bound.");
            buffer.Write(chunk, 0, read);
        }
        var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new JsonException("Last.fm response is malformed.");
        }
        if (document.RootElement.TryGetProperty("error", out var error))
        {
            var validCode = int.TryParse(error.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var code);
            document.Dispose();
            if (!validCode) throw new JsonException("Last.fm error code is malformed.");
            throw new SourceFailure(code switch
            {
                17 => ProviderError.LastFmPrivateProfile(),
                10 or 26 => ProviderError.LastFmApiKeyRequired(),
                4 or 9 => new(ProviderErrorKind.AccountNeedsReauthentication),
                29 => new(ProviderErrorKind.RateLimited, retryAfter),
                8 or 11 or 16 => new(ProviderErrorKind.TransientFailure),
                _ => new(ProviderErrorKind.PermanentFailure)
            });
        }
        return document;
    }

    private static IEnumerable<Track> ParseTracks(JsonElement container) => LastFmSimilarTracks.TrackElements(container).Select(item =>
    {
        var title = Text(item, "name") ?? throw new JsonException("Last.fm track title is missing.");
        var credit = item.TryGetProperty("artist", out var artist) ? artist : default;
        var name = Text(credit, "name") ?? throw new JsonException("Last.fm track artist is missing.");
        var mbid = Guid.TryParse(Text(item, "mbid"), out var parsed) && parsed != Guid.Empty ? parsed.ToString("D") : null;
        return new Track(title, name, mbid);
    });

    private static ProviderPlaylistSummary Summary(ProviderExternalResourceId id, string username, string revision, int? count, string? reason)
    {
        var (kind, period, _) = Selection(id.Value);
        var name = kind switch
        {
            "loved" => "Loved on Last.fm",
            "similar" => "Similar to your top tracks",
            _ => $"Your top tracks — {PeriodLabel(period!)}"
        };
        var description = kind switch
        {
            "loved" => "Your loved tracks on Last.fm, up to 1,000 tracks.",
            "similar" => "Track suggestions from Last.fm, based on your top 10 tracks of the last month.",
            _ => $"Your top tracks on Last.fm for {PeriodLabel(period!)}."
        };
        return new(id, name, new(username), revision, reason == null ? description : $"{description} {reason}", trackCount: count);
    }

    private static (string Kind, string? Period, int Limit) Selection(string id)
    {
        if (id == "loved") return (id, null, 1000);
        if (id == "similar") return (id, "1month", 50);
        var parts = id.Split(':');
        if (parts.Length is 2 or 3 && parts[0] == "top" && Periods.Contains(parts[1]) &&
            (parts.Length == 2 || int.TryParse(parts[2], out var count) && count is >= 25 and <= 100))
            return ("top", parts[1], parts.Length == 2 ? 50 : int.Parse(parts[2], CultureInfo.InvariantCulture));
        throw new ArgumentException("Unknown Last.fm playlist selection.");
    }

    private static string PeriodLabel(string period) => period switch
    {
        "7day" => "7 days",
        "1month" => "1 month",
        "3month" => "3 months",
        "12month" => "12 months",
        _ => "all time"
    };
    private static int Offset(string? cursor) => cursor == null ? 0 :
        int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value is >= 0 and <= 1000
            ? value : throw new ArgumentException("Invalid Last.fm playlist cursor.");
    private static ProviderExternalResourceId Id(string id) => new(StableProviderId, ProviderResourceKind.Playlist, id);
    private static string Key(Track track) => $"{track.Artist.Trim().ToLowerInvariant()}\n{track.Title.Trim().ToLowerInvariant()}";
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string? Text(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()!.Trim() : null;
    private static JsonElement Container(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var container) && container.ValueKind == JsonValueKind.Object
            ? container : throw new JsonException("Last.fm track list is missing.");
    private sealed record Track(string Title, string Artist, string? Mbid);
    private sealed class SourceFailure(ProviderError error) : Exception
    {
        public ProviderError Error { get; } = error;
    }
}
