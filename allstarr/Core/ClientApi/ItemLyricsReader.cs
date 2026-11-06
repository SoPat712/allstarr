using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Lyrics;
using allstarr.Services.Jellyfin;
using allstarr.Services.Local;

namespace allstarr.Core.ClientApi;

public sealed class ItemLyricsReader(
    ILocalLibraryService localLibrary,
    JellyfinProxyService jellyfin,
    IProtocolProviderGateway providers,
    IProtocolLyricsResolver lyrics,
    ILibraryIndexService library,
    ITrackIdentityService identities,
    IRecordingLyricsReview review,
    IRecordingLyricsChoices choices,
    IProviderRegistry? registry = null)
{
    public async Task<Guid?> ResolveRecordingIdAsync(HttpContext http, ProtocolExecutionContext execution, string itemId)
    {
        var item = await ResolveAsync(http, execution, itemId);
        return item == null ? null : await RecordingAsync(execution, item);
    }

    public async Task<RichLyrics?> ReadAsync(HttpContext http, ProtocolExecutionContext execution, string itemId, string? sourceId = null)
    {
        var item = await ResolveAsync(http, execution, itemId);
        if (item == null) return null;
        if (sourceId == null) return await ReadRichAsync(execution with { RequestedItemId = itemId }, item);
        var recording = await RecordingAsync(execution, item);
        return recording == null ? null : await review.ReadCandidateAsync(execution, recording.Value, sourceId,
            (id, token) => ReadBackendAsync(http, id, token));
    }

    public async Task<ClientLyricsSources?> SourcesAsync(HttpContext http, ProtocolExecutionContext execution, string itemId)
    {
        var item = await ResolveAsync(http, execution, itemId);
        if (item == null) return null;
        var layers = await ChoicesAsync(execution, item);
        var candidates = await lyrics.FindAllAsync(execution with { RequestedItemId = itemId }, item.Song, item.ResourceKey, item.Sources);
        var sources = ProtocolLyricsResolver.Assess(candidates, item.Song).Select(candidate =>
        {
            var rich = RichLyricsMapper.Map(candidate.Lyrics);
            return new { Candidate = candidate, Rich = rich == null ? null : rich with { Source = new(candidate.SourceId, RichLyricsMapper.SourceName(candidate.SourceId)) } };
        }).ToArray();
        var known = sources.Where(source => source.Rich != null).ToArray();
        var ranked = known.Where(source => source.Candidate.Assessment != null)
            .OrderByDescending(source => source.Candidate.Assessment!.Score).Select((source, index) => new { source.Candidate.SourceId, Rank = index + 1 })
            .ToDictionary(source => source.SourceId, source => source.Rank, StringComparer.Ordinal);
        var defaults = known.Where(source => source.Candidate.IsDefaultVersion).ToArray();
        var selected = layers.Effective is { } preference
            ? known.FirstOrDefault(source => source.Candidate.SourceId == preference.SourceId) ??
                defaults.FirstOrDefault(source => LyricsVersions.ProviderId(source.Candidate.SourceId) == LyricsVersions.ProviderId(preference.SourceId)) ??
                known.FirstOrDefault(source => LyricsVersions.ProviderId(source.Candidate.SourceId) == LyricsVersions.ProviderId(preference.SourceId))
            : null;
        selected ??= known.Where(source => ranked.ContainsKey(source.Candidate.SourceId)).OrderBy(source => ranked[source.Candidate.SourceId]).FirstOrDefault();
        return new(sources.Select(source => new ClientLyricsSource(source.Candidate.SourceId, source.Rich?.Format, ShortPreview(source.Rich),
            LyricsVersions.ProviderId(source.Candidate.SourceId), ProviderName(source.Candidate.SourceId), source.Candidate.Lyrics?.Metadata?.Title,
            source.Candidate.Lyrics?.Metadata?.Artist, source.Candidate.Lyrics?.Metadata?.Album, source.Candidate.Lyrics?.Metadata?.DurationMs)
        {
            State = source.Rich != null ? "ready" : source.Candidate.State == "pending" ? "pending" : "unavailable",
            Score = source.Candidate.Assessment?.Score,
            Rank = ranked.TryGetValue(source.Candidate.SourceId, out var rank) ? rank : null,
            Reasons = source.Candidate.Assessment?.Reasons
        }).ToArray(),
            Chosen(layers, selected?.Rich), ClientLyricsRevision.From(layers.Revision));
    }

    private async Task<RecordingLyricsChoiceLayers> ChoicesAsync(ProtocolExecutionContext execution, LyricsItem item)
    {
        try
        {
            var recording = await RecordingAsync(execution, item);
            if (recording != null) return await choices.GetAsync(execution, recording.Value);
        }
        catch (KeyNotFoundException) { }
        catch (UnauthorizedAccessException) { }
        return new(null, null);
    }

    private static string? ShortPreview(RichLyrics? rich)
    {
        if (rich == null) return null;
        var preview = string.Join("\n", rich.Lines.Take(4).Select(line => line.Text));
        return preview.Length <= 1000 ? preview : preview[..(char.IsHighSurrogate(preview[999]) ? 999 : 1000)];
    }

    public async Task<ClientLyricsSelection?> SetAsync(HttpContext http, ProtocolExecutionContext execution, string itemId,
        SetClientLyricsSourceRequest request)
    {
        var item = await ResolveAsync(http, execution, itemId);
        var recording = item == null ? null : await RecordingAsync(execution, item);
        if (item == null || recording == null) return null;
        var result = await review.GetAsync(execution, recording.Value, "personal",
            (id, token) => ReadBackendAsync(http, id, token));
        var source = RecordingLyricsReview.SelectSource(result.Sources, request.SourceId);
        if (source?.PreviewRevision == null)
            throw new LyricsChoiceConflictException("The lyrics source is unavailable.");
        var choices = await review.ChooseAsync(execution, recording.Value, request.SourceId, "personal",
            request.ExpectedRevision.ToExpected(), source.PreviewRevision, (id, token) => ReadBackendAsync(http, id, token));
        return new(Chosen(choices, await ReadRichAsync(execution, item)), ClientLyricsRevision.From(choices.Revision));
    }

    public async Task<ClientLyricsSelection?> ClearAsync(HttpContext http, ProtocolExecutionContext execution, string itemId,
        ClearClientLyricsSourceRequest request)
    {
        var item = await ResolveAsync(http, execution, itemId);
        var recording = item == null ? null : await RecordingAsync(execution, item);
        if (item == null || recording == null) return null;
        var choices = await review.ClearAsync(execution, recording.Value, "personal", request.ExpectedRevision.ToExpected());
        return new(Chosen(choices, await ReadRichAsync(execution, item)), ClientLyricsRevision.From(choices.Revision));
    }

    private string ProviderName(string sourceId)
    {
        var provider = LyricsVersions.ProviderId(sourceId);
        var name = RichLyricsMapper.SourceName(sourceId);
        return name == provider && registry?.TryGet(provider, out var descriptor) == true ? descriptor!.DisplayName : name;
    }

    private Task<RichLyrics?> ReadRichAsync(ProtocolExecutionContext execution, LyricsItem item) =>
        lyrics.GetRichLyricsAsync(execution, item.Song, item.ResourceKey, item.Sources);

    private static ClientLyricsChosen? Chosen(RecordingLyricsChoiceLayers choices, RichLyrics? rich)
    {
        if (rich == null) return null;
        var layer = choices.Effective is { } effective &&
            (effective.SourceId == rich.Source.Id || LyricsVersions.ProviderId(effective.SourceId) == LyricsVersions.ProviderId(rich.Source.Id))
            ? choices.Personal == null ? "household" : "personal" : "automatic";
        return new(rich.Source.Id, layer);
    }

    private async Task<Guid?> RecordingAsync(ProtocolExecutionContext execution, LyricsItem item)
    {
        if (item.Song.IsLocal)
            return (await library.FindByBackendItemAsync(execution, item.Song.Id, execution.CancellationToken))?.CanonicalRecordingId;
        var publicId = string.IsNullOrWhiteSpace(item.Song.Id)
            ? $"ext-{item.Song.ExternalProvider}-song-{item.Song.ExternalId}" : item.Song.Id;
        return item.Song.CanonicalRecordingId ??
            (await identities.ResolveRecordingAsync(execution.RequireActor(), publicId, execution.CancellationToken))?.Id;
    }

    private async Task<LyricsItem?> ResolveAsync(HttpContext http, ProtocolExecutionContext execution, string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId)) return null;
        var (external, provider, externalId) = localLibrary.ParseSongId(itemId);
        if (external)
        {
            var song = await providers.GetSongAsync(execution, provider!, externalId!);
            if (song == null) return null;
            var lookup = MusicLyrics.ForLookup(song);
            lookup.ExternalProvider = provider;
            lookup.ExternalId = externalId;
            return new(lookup, $"{provider}:{externalId}", new());
        }

        var (body, _) = await jellyfin.GetItemAsync(itemId, http.Request.Headers, execution.CancellationToken);
        using var document = body;
        if (body == null || body.RootElement.ValueKind != JsonValueKind.Object ||
            !body.RootElement.TryGetProperty("Id", out var nativeId) || nativeId.ValueKind != JsonValueKind.String ||
            !SameNativeItem(nativeId.GetString(), itemId) || !body.RootElement.TryGetProperty("Type", out var type) ||
            type.GetString() != "Audio") return null;
        var item = body.RootElement;
        var native = new Song
        {
            Id = itemId,
            IsLocal = true,
            Title = item.TryGetProperty("Name", out var name) ? name.GetString() ?? "" : "",
            Artist = item.TryGetProperty("AlbumArtist", out var artist) ? artist.GetString() ?? "" : "",
            Album = item.TryGetProperty("Album", out var album) ? album.GetString() ?? "" : "",
            AlbumId = item.TryGetProperty("AlbumId", out var albumId) ? albumId.GetString() : null,
            Duration = item.TryGetProperty("RunTimeTicks", out var ticks) ? (int)(ticks.GetInt64() / 10000000) : 0
        };
        if (item.TryGetProperty("ProviderIds", out var ids) && ids.TryGetProperty("Spotify", out var spotify))
            native.SpotifyId = spotify.GetString();
        var headers = CopyHeaders(http);
        return new(MusicLyrics.ForLookup(native), itemId,
            new(Backend: token => ReadBackendAsync(headers, itemId, token)));
    }

    private async Task<LyricsInfo?> ReadBackendAsync(HttpContext http, string itemId, CancellationToken token)
        => await ReadBackendAsync(CopyHeaders(http), itemId, token);

    private static IHeaderDictionary CopyHeaders(HttpContext http) => new HeaderDictionary(
        http.Request.Headers.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase));

    private async Task<LyricsInfo?> ReadBackendAsync(IHeaderDictionary headers, string itemId, CancellationToken token)
    {
        var (body, status) = await jellyfin.GetJsonAsync($"Audio/{Uri.EscapeDataString(itemId)}/Lyrics", null, headers, token);
        using (body)
            return status == StatusCodes.Status200OK && body != null ? BackendLyrics.ParseJellyfin(body.RootElement) : null;
    }

    internal static bool SameNativeItem(string? actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase) ||
        Guid.TryParse(actual, out var actualGuid) && Guid.TryParse(expected, out var expectedGuid) && actualGuid == expectedGuid;

    private sealed record LyricsItem(Song Song, string ResourceKey, ProtocolLyricsSources Sources);
}
