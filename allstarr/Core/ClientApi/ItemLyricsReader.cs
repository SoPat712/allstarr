using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using System.Text.Json.Nodes;
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
    IProviderRegistry? registry = null, IEffectiveProviderPolicyResolver? policies = null, TimeProvider? timeProvider = null)
{
    public async Task<Guid?> ResolveRecordingIdAsync(HttpContext http, ProtocolExecutionContext execution, string itemId)
    {
        var item = await ResolveAsync(http, execution, itemId);
        return item == null ? null : await RecordingAsync(execution, item);
    }

    public async Task<RichLyrics?> ReadAsync(HttpContext http, ProtocolExecutionContext execution, string itemId, string? sourceId = null)
    {
        execution = WithLyricsDeadline(execution);
        var item = await ResolveAsync(http, execution, itemId);
        if (item == null) return null;
        if (sourceId == null) return await ReadRichAsync(execution with { RequestedItemId = itemId }, item);
        if (sourceId.Length == 0) return null;
        return await ReadVersionAsync(execution, item, itemId, sourceId);
    }

    // One version comes from the same lookup the version list uses, so every listed version can be read and pinned.
    private async Task<RichLyrics?> ReadVersionAsync(ProtocolExecutionContext execution, LyricsItem item, string itemId, string sourceId)
    {
        var candidates = await lyrics.FindAllAsync(execution with { RequestedItemId = itemId }, item.Song, item.ResourceKey,
            item.Sources with { OnlySourceId = sourceId, ReadOnly = true });
        var candidate = candidates.FirstOrDefault(candidate => candidate.SourceId == sourceId) ??
            (sourceId.Contains(':') ? null : candidates.FirstOrDefault(candidate => LyricsVersions.ProviderId(candidate.SourceId) == sourceId));
        return candidate == null ? null : Usable(Rich(candidate));
    }

    public async Task<ClientLyricsSources?> SourcesAsync(HttpContext http, ProtocolExecutionContext execution, string itemId,
        bool includeLyrics = false)
    {
        execution = WithLyricsDeadline(execution);
        var item = await ResolveAsync(http, execution, itemId);
        if (item == null) return null;
        var layers = await ChoicesAsync(execution, item);
        var recordingDurationMs = await RecordingDurationMsAsync(execution, item);
        var candidates = await lyrics.FindAllAsync(execution with { RequestedItemId = itemId }, item.Song, item.ResourceKey, item.Sources);
        var scored = recordingDurationMs is > 0
            ? candidates.Select(candidate => candidate with { Assessment = null }).ToArray()
            : candidates;
        var sources = ProtocolLyricsResolver.Assess(scored, item.Song, recordingDurationMs).Select((candidate, order) =>
        {
            var rich = Usable(Rich(candidate));
            return new
            {
                Candidate = candidate,
                Rich = rich,
                Order = order,
                Score = rich == null ? null : candidate.Assessment ?? LyricsQuality.Evaluate(candidate.Lyrics, item.Song, recordingDurationMs)
            };
        }).Where(source => source.Rich != null || source.Candidate.State == "pending").ToArray();
        var known = sources.Where(source => source.Rich != null).ToArray();
        // Every listed version gets a rank: match confidence (which already favours word over line over plain), then timing.
        var ranked = known.OrderByDescending(source => source.Score?.Score ?? -1)
            .ThenByDescending(source => LyricsQuality.Rank(source.Rich!.Format))
            .ThenByDescending(source => source.Candidate.IsDefaultVersion).ThenBy(source => source.Order)
            .Select((source, index) => new { source.Candidate.SourceId, Rank = index + 1 })
            .ToDictionary(source => source.SourceId, source => source.Rank, StringComparer.Ordinal);
        var defaults = known.Where(source => source.Candidate.IsDefaultVersion).ToArray();
        var selected = layers.Effective is { } preference
            ? known.FirstOrDefault(source => source.Candidate.SourceId == preference.SourceId) ??
                defaults.FirstOrDefault(source => LyricsVersions.ProviderId(source.Candidate.SourceId) == LyricsVersions.ProviderId(preference.SourceId)) ??
                known.FirstOrDefault(source => LyricsVersions.ProviderId(source.Candidate.SourceId) == LyricsVersions.ProviderId(preference.SourceId))
            : null;
        selected ??= known.Where(source => source.Candidate.Assessment != null).OrderBy(source => ranked[source.Candidate.SourceId]).FirstOrDefault();
        return new(sources.OrderBy(source => source.Rich == null ? int.MaxValue : ranked[source.Candidate.SourceId]).Select(source =>
            new ClientLyricsSource(source.Candidate.SourceId, source.Rich?.Format, ShortPreview(source.Rich),
            LyricsVersions.ProviderId(source.Candidate.SourceId), ProviderName(source.Candidate.SourceId), source.Candidate.Lyrics?.Metadata?.Title,
            source.Candidate.Lyrics?.Metadata?.Artist, source.Candidate.Lyrics?.Metadata?.Album, source.Candidate.Lyrics?.Metadata?.DurationMs)
            {
                State = source.Rich != null ? "ready" : "pending",
                Score = source.Score?.Score,
                Rank = ranked.TryGetValue(source.Candidate.SourceId, out var rank) ? rank : null,
                Reasons = source.Score?.Reasons,
                Lyrics = includeLyrics ? source.Rich : null
            }).ToArray(),
            Chosen(layers, selected?.Rich), ClientLyricsRevision.From(layers.Revision));
    }

    private ProtocolExecutionContext WithLyricsDeadline(ProtocolExecutionContext execution)
    {
        var deadline = (timeProvider ?? TimeProvider.System).GetUtcNow().AddSeconds(3);
        return ProtocolLyricsResolver.WithLyricsToken(execution, execution.CancellationToken,
            deadline < execution.Deadline ? deadline : execution.Deadline);
    }

    private static RichLyrics? Rich(ProtocolLyricsCandidate candidate) => RichLyricsMapper.Map(candidate.Lyrics) is { } rich
        ? rich with { Source = new(candidate.SourceId, RichLyricsMapper.SourceName(candidate.SourceId)) } : null;

    // A version without a single line of text is not worth offering.
    private static RichLyrics? Usable(RichLyrics? rich) => rich?.Lines.Any(line => HasWords(line.Text)) == true ? rich : null;

    private static bool HasWords(string? text) => text?.Any(char.IsLetterOrDigit) == true;

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
        // Instrumental markers and blank lines would leave the preview empty.
        var preview = string.Join("\n", rich.Lines.Where(line => HasWords(line.Text)).Take(4).Select(line => line.Text.Trim()));
        return preview.Length <= 1000 ? preview : preview[..(char.IsHighSurrogate(preview[999]) ? 999 : 1000)];
    }

    public async Task<ClientLyricsSelection?> SetAsync(HttpContext http, ProtocolExecutionContext execution, string itemId,
        SetClientLyricsSourceRequest request)
    {
        var item = await ResolveAsync(http, execution, itemId);
        var recording = item == null ? null : await RecordingAsync(execution, item);
        if (item == null || recording == null) return null;
        // A pin names a source, not one copy of its text, so it only needs the source to still offer this song.
        if (await ReadVersionAsync(execution, item, itemId, request.SourceId) == null)
            throw new LyricsChoiceConflictException("The lyrics source is unavailable.");
        var saved = await choices.SetAsync(execution, recording.Value, request.SourceId, "personal", request.ExpectedRevision.ToExpected());
        return new(Chosen(saved, await ReadRichAsync(execution, item)), ClientLyricsRevision.From(saved.Revision));
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

    private async Task<long?> RecordingDurationMsAsync(ProtocolExecutionContext execution, LyricsItem item)
    {
        if (execution.Actor == null) return null;
        try
        {
            var recording = item.Song.CanonicalRecordingId ?? await RecordingAsync(execution, item);
            if (recording == null) return null;
            var duration = (await identities.ResolveRecordingAsync(execution.RequireActor(), recording.Value.ToString("D"),
                execution.CancellationToken))?.DurationMilliseconds;
            return duration is > 0 ? duration : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<LyricsItem?> ResolveAsync(HttpContext http, ProtocolExecutionContext execution, string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId)) return null;
        var policy = policies != null && execution.Actor?.EffectiveUserId is { } owner
            ? await policies.ResolveForUserAsync(owner, execution.CancellationToken) : execution.Policy;
        var (external, provider, externalId) = localLibrary.ParseSongId(itemId);
        if (external)
        {
            var song = await providers.GetSongAsync(execution, provider!, externalId!);
            if (song == null || policy?.Includes(song) == false) return null;
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
        var rating = CleanOnlyProtocolContent.Rating((JsonObject)JsonNode.Parse(item.GetRawText())!, ProtocolKind.Jellyfin);
        if (policy?.Includes(rating) == false) return null;
        var native = new Song
        {
            Id = itemId,
            IsLocal = true,
            ExplicitContentLyrics = rating,
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
        {
            if (status >= 500) throw new HttpRequestException("Library lyrics are unavailable.");
            return status == StatusCodes.Status200OK && body != null ? BackendLyrics.ParseJellyfin(body.RootElement) : null;
        }
    }

    internal static bool SameNativeItem(string? actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase) ||
        Guid.TryParse(actual, out var actualGuid) && Guid.TryParse(expected, out var expectedGuid) && actualGuid == expectedGuid;

    private sealed record LyricsItem(Song Song, string ResourceKey, ProtocolLyricsSources Sources);
}
