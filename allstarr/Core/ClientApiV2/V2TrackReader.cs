using System.Text.Json;
using allstarr.Core.ClientApi;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Jellyfin;
using allstarr.Services.Local;

namespace allstarr.Core.ClientApiV2;

/// <summary>
/// Builds the one native track shape. A song in the library is read from the library, so local files always win;
/// everything else comes from the stored service metadata. Saved, plays, keep, match and lyrics come from the same
/// reader the v1 item details use, which also applies the listener's source and explicit-content choices.
/// </summary>
public sealed class V2TrackReader(
    ItemDetailsReader details,
    JellyfinProxyService jellyfin,
    ITrackIdentityService identities,
    IProviderRegistry registry,
    IProtocolProviderGateway gateway,
    ILibraryIndexService libraryIndex)
{
    private const string LibraryFields = "MediaSources,ProviderIds";
    private static readonly HashSet<string> LosslessCodecs = new(StringComparer.OrdinalIgnoreCase)
        { "flac", "alac", "wav", "aiff", "ape", "wavpack", "pcm_s16le", "pcm_s24le", "pcm_s32le" };

    public async Task<V2TracksResponse> ReadAsync(HttpContext http, ProtocolExecutionContext execution, IReadOnlyList<string> ids,
        bool includeAlbums) => (await ReadWithSourcesAsync(http, execution, ids, includeAlbums)).Response;

    /// <summary>Also returns, for each returned track id, the library or service item it was read from.</summary>
    internal async Task<(V2TracksResponse Response, IReadOnlyDictionary<string, string> Sources)> ReadWithSourcesAsync(HttpContext http,
        ProtocolExecutionContext execution, IReadOnlyList<string> ids, bool includeAlbums)
    {
        var token = execution.CancellationToken;
        var requested = ids.Distinct(StringComparer.Ordinal).ToArray();
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in requested)
            if (V2Ids.TryDecode(id, V2IdKind.Track, out var decoded))
                sources[id] = decoded.Recording is { } recording ? CanonicalRecordingRecord.DefaultPublicId(recording) : decoded.Source!;

        // A service song that is also in the library reads as the library copy.
        var external = sources.Values.Where(IsExternal).Distinct(StringComparer.Ordinal).ToArray();
        var copies = new Dictionary<string, string>(StringComparer.Ordinal);
        if (external.Length > 0)
        {
            var known = await identities.ResolveRecordingsAsync(execution.RequireActor(), external, token);
            var lookups = external.Where(id => known.GetValueOrDefault(id) != null).Select(id =>
            {
                var parsed = LocalLibraryService.ParseExternalResource(id);
                var identity = known[id]!;
                return new LibraryRecordingLookup(id, identity.Id, identity.Isrc, registry.ResolveProviderId(parsed.provider!), parsed.externalId);
            }).ToArray();
            if (lookups.Length > 0)
                foreach (var pair in await libraryIndex.FindKnownRecordingTracksAsync(execution, lookups, token)) copies[pair.Key] = pair.Value;
        }
        var targets = sources.ToDictionary(pair => pair.Key, pair => copies.GetValueOrDefault(pair.Value) ?? pair.Value, StringComparer.Ordinal);
        var targetIds = targets.Values.Distinct(StringComparer.Ordinal).ToArray();

        // Facts also decide what this listener may see: anything they leave out is reported missing.
        var facts = targetIds.Length == 0 ? new Dictionary<string, ItemDetails>(StringComparer.Ordinal)
            : (await details.ReadAsync(http, execution, targetIds)).Items.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var library = await ReadLibraryAsync(http, execution, targetIds.Where(id => !IsExternal(id) && facts.ContainsKey(id)).ToArray());
        // Stored metadata answers almost every song; only a song never seen before asks its service, a few at a time.
        using var gate = new SemaphoreSlim(4);
        var songs = (await Task.WhenAll(targetIds.Where(id => IsExternal(id) && facts.ContainsKey(id)).Select(async id =>
        {
            await gate.WaitAsync(token);
            try { return (Id: id, Song: await ReadSongAsync(execution, id)); }
            finally { gate.Release(); }
        }))).Where(item => item.Song != null).ToDictionary(item => item.Id, item => item.Song!, StringComparer.Ordinal);

        var tracks = new List<V2Track>();
        var albums = includeAlbums ? new Dictionary<string, V2AlbumSummary>(StringComparer.Ordinal) : null;
        var missing = new List<string>();
        var returned = new Dictionary<string, int>(StringComparer.Ordinal);
        var readFrom = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in requested)
        {
            var target = targets.GetValueOrDefault(id);
            var item = target == null ? null : facts.GetValueOrDefault(target);
            var built = item == null ? null : library.TryGetValue(target!, out var local) ? FromLibrary(target!, local, item, albums)
                : songs.TryGetValue(target!, out var song) ? FromSong(target!, song, item, albums) : null;
            if (built == null)
            {
                missing.Add(id);
                continue;
            }
            var aliases = Aliases(built.Id, id, sources[id], target!, item!.CanonicalRecordingId);
            // Two spellings of one song return it once, answering to both.
            if (returned.TryGetValue(built.Id, out var index))
                tracks[index] = tracks[index] with { Aliases = tracks[index].Aliases.Union(aliases, StringComparer.Ordinal).ToArray() };
            else
            {
                returned[built.Id] = tracks.Count;
                readFrom[built.Id] = target!;
                tracks.Add(built with { Aliases = aliases });
            }
        }
        return (new(tracks, missing) { Albums = albums }, readFrom);
    }

    private static bool IsExternal(string id) => id.StartsWith("ext-", StringComparison.Ordinal);

    private static string TrackId(string target, string? recording) =>
        Guid.TryParse(recording, out var parsed) ? V2Ids.Track(parsed) : V2Ids.TrackSource(target);

    /// <summary>Every other id this track answers to, so a client can match ids it already holds from older routes.</summary>
    private static IReadOnlyList<string> Aliases(string id, string requested, string source, string target, string? recording) =>
        new[] { requested, source, target, Guid.TryParse(recording, out var parsed) ? CanonicalRecordingRecord.DefaultPublicId(parsed) : null }
            .OfType<string>().Where(alias => alias != id).Distinct(StringComparer.Ordinal).ToArray();

    private async Task<Song?> ReadSongAsync(ProtocolExecutionContext execution, string id)
    {
        var parsed = LocalLibraryService.ParseExternalResource(id);
        try
        {
            var song = await gateway.GetStoredSongAsync(execution, parsed.provider!, parsed.externalId!) ??
                await gateway.GetSongAsync(execution, parsed.provider!, parsed.externalId!);
            return string.IsNullOrWhiteSpace(song?.Title) ? null : song;
        }
        catch (Exception error) when (error is not OperationCanceledException) { return null; }
    }

    private static V2Track Compose(string id, ItemDetails item, string? libraryItemId, IReadOnlyDictionary<string, string> externalIds,
        string title, IReadOnlyList<V2Ref> artists, IReadOnlyList<V2Ref> albumArtists, V2Ref? album, int? disc, int? track,
        long? durationMs, int? year, V2Artwork? artwork, V2Format? format, double? trackGain, double? albumGain) =>
        new(id, [], item.IsExternal, libraryItemId,
            item.Provider == null ? null : new(item.Provider.Id, item.Provider.Name, item.Provider.IconUrl, item.Provider.GlyphUrl),
            externalIds, title, artists, albumArtists, Featuring(artists, albumArtists), album, disc, track, durationMs, year,
            item.Explicit, artwork, format,
            Finite(trackGain), Finite(albumGain), item.Saved, item.PlayCount, item.LastPlayedAt,
            new(item.Keep.State, item.Keep.Progress),
            item.Match == null ? null : new(item.Match.State, item.Match.Confidence, item.Match.Authority),
            new(item.Lyrics.Available, item.Lyrics.Format, item.Lyrics.Source));

    private static V2Track FromLibrary(string target, LibraryItem local, ItemDetails item, Dictionary<string, V2AlbumSummary>? albums)
    {
        var externalIds = new Dictionary<string, string>(StringComparer.Ordinal);
        if (item.Isrc is { Length: > 0 } isrc) externalIds["isrc"] = isrc;
        if (local.MusicBrainzRecording is { } musicBrainz) externalIds["musicbrainzRecording"] = musicBrainz;
        var album = local.AlbumId is { } albumId && V2Ids.IsValidSource(albumId, V2IdKind.Album) && local.Album is { } albumName
            ? new V2Ref(V2Ids.Album(albumId), albumName) : null;
        var albumArtwork = V2ArtworkUrls.For(local.AlbumId, local.AlbumImageTag);
        if (album != null && albums != null)
            albums.TryAdd(album.Id!, new(album.Id!, album.Name, local.AlbumArtists, local.Year, false, albumArtwork));
        var format = local.Format;
        if (format == null && item.Audio is { } audio) format = FormatOf(null, audio.Codec, audio.Bitrate, audio.SampleRate, audio.BitDepth, null);
        return Compose(TrackId(target, item.CanonicalRecordingId), item, target, externalIds, local.Title, local.Artists,
            local.AlbumArtists, album, local.Disc, local.Track, local.DurationMs, local.Year,
            V2ArtworkUrls.For(target, local.ImageTag) ?? albumArtwork, format, local.TrackGain, null) with
        { SourceVersion = local.SourceVersion };
    }

    private static V2Track FromSong(string target, Song song, ItemDetails item, Dictionary<string, V2AlbumSummary>? albums)
    {
        var externalIds = new Dictionary<string, string>(StringComparer.Ordinal);
        if (item.Isrc is { Length: > 0 } isrc) externalIds["isrc"] = isrc;
        if (song.ExternalProvider is { Length: > 0 } provider && song.ExternalId is { Length: > 0 } externalId && provider != "allstarr")
            externalIds[provider] = externalId;
        var names = song.Artists.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim()).ToList();
        if (names.Count == 0 && !string.IsNullOrWhiteSpace(song.Artist)) names.Add(song.Artist.Trim());
        var artists = names.Select((name, index) => new V2Ref(names.Count == song.ArtistIds.Count ? ArtistId(song.ArtistIds[index])
            : index == 0 ? ArtistId(song.ArtistId) : null, name)).ToArray();
        var albumArtistName = !string.IsNullOrWhiteSpace(song.AlbumArtist) ? song.AlbumArtist.Trim() : names.FirstOrDefault();
        V2Ref[] albumArtists = albumArtistName == null ? [] : [new(ArtistId(song.ArtistId), albumArtistName)];
        var album = song.AlbumId is { } albumId && V2Ids.IsValidSource(albumId, V2IdKind.Album) && !string.IsNullOrWhiteSpace(song.Album)
            ? new V2Ref(V2Ids.Album(albumId), song.Album) : null;
        var artwork = V2ArtworkUrls.For(target, JellyfinItemArtwork.PrimaryImageTag(target, false));
        if (album != null && albums != null)
            albums.TryAdd(album.Id!, new(album.Id!, album.Name, albumArtists, song.Year, true,
                V2ArtworkUrls.For(song.AlbumId, JellyfinItemArtwork.AlbumImageTag(song.AlbumId!, false))));
        var duration = song.PreciseDuration is { TotalMilliseconds: > 0 } precise ? (long)precise.TotalMilliseconds
            : song.Duration is > 0 ? song.Duration.Value * 1000L : (long?)null;
        var format = item.Audio is { } audio ? FormatOf(null, audio.Codec, audio.Bitrate, audio.SampleRate, audio.BitDepth, null) : null;
        return Compose(TrackId(target, item.CanonicalRecordingId), item, null, externalIds, song.Title, artists, albumArtists, album,
            song.DiscNumber is > 0 ? song.DiscNumber : null, song.Track is > 0 ? song.Track : null, duration, song.Year, artwork, format,
            song.ReplayGain?.TrackGain, song.ReplayGain?.AlbumGain);
    }

    /// <summary>The first artist leads the track, so a compilation's lead artist is never listed as featured.</summary>
    private static IReadOnlyList<V2Ref> Featuring(IReadOnlyList<V2Ref> artists, IReadOnlyList<V2Ref> albumArtists) =>
        artists.Skip(1).Where(artist => !albumArtists.Any(albumArtist => artist.Id != null && albumArtist.Id == artist.Id ||
            string.Equals(albumArtist.Name, artist.Name, StringComparison.OrdinalIgnoreCase))).ToArray();

    private static string? ArtistId(string? source) =>
        source != null && V2Ids.IsValidSource(source, V2IdKind.Artist) ? V2Ids.Artist(source) : null;

    private static double? Finite(double? value) => value is { } number && double.IsFinite(number) ? number : null;

    private static V2Format? FormatOf(string? container, string? codec, int? bitrate, int? sampleRate, int? bitDepth, int? channels) =>
        container == null && codec == null && bitrate == null && sampleRate == null && bitDepth == null ? null :
            new(container, codec?.ToLowerInvariant(), bitrate, sampleRate, bitDepth, channels,
                codec == null ? null : LosslessCodecs.Contains(codec));

    private async Task<Dictionary<string, LibraryItem>> ReadLibraryAsync(HttpContext http, ProtocolExecutionContext execution, string[] ids)
    {
        var result = new Dictionary<string, LibraryItem>(StringComparer.Ordinal);
        foreach (var batch in ids.Chunk(V2Contract.MaximumIdsPerRequest))
        {
            var (body, status) = await jellyfin.GetJsonAsync($"Users/{Uri.EscapeDataString(execution.VerifiedBackendPrincipalId)}/Items",
                new() { ["ids"] = string.Join(',', batch), ["limit"] = "100", ["fields"] = LibraryFields, ["enableUserData"] = "false" },
                http.Request.Headers, execution.CancellationToken);
            using var document = body;
            if (status is 400 or 401 or 403 or 404) continue;
            if (status != 200 || document == null) throw new HttpRequestException("Library details are unavailable.");
            if (!document.RootElement.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array) continue;
            foreach (var element in items.EnumerateArray())
            {
                if (String(element, "Id") is not { } id || String(element, "Type") != "Audio") continue;
                var requested = batch.FirstOrDefault(candidate => candidate == id ||
                    Guid.TryParse(candidate, out var first) && Guid.TryParse(id, out var second) && first == second);
                if (requested != null) result[requested] = LibraryItem.Read(id, element);
            }
        }
        return result;
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static IReadOnlyList<V2Ref> Refs(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(artist => (Name: String(artist, "Name"), Id: String(artist, "Id")))
                .Where(artist => !string.IsNullOrWhiteSpace(artist.Name))
                .Select(artist => new V2Ref(ArtistId(artist.Id), artist.Name!)).ToArray()
            : [];

    private sealed record LibraryItem(string Title, IReadOnlyList<V2Ref> Artists, IReadOnlyList<V2Ref> AlbumArtists, string? AlbumId,
        string? Album, string? AlbumImageTag, string? ImageTag, int? Disc, int? Track, long? DurationMs, int? Year, V2Format? Format,
        double? TrackGain, string? MusicBrainzRecording, string? SourceVersion)
    {
        internal static LibraryItem Read(string id, JsonElement element)
        {
            var ticks = element.TryGetProperty("RunTimeTicks", out var runtime) && runtime.ValueKind == JsonValueKind.Number &&
                runtime.TryGetInt64(out var value) && value > 0 ? value : (long?)null;
            var imageTag = element.TryGetProperty("ImageTags", out var tags) && tags.ValueKind == JsonValueKind.Object ? String(tags, "Primary") : null;
            var gain = element.TryGetProperty("NormalizationGain", out var normalization) && normalization.ValueKind == JsonValueKind.Number
                ? normalization.GetDouble() : (double?)null;
            var musicBrainz = element.TryGetProperty("ProviderIds", out var providers) && providers.ValueKind == JsonValueKind.Object
                ? String(providers, "MusicBrainzTrack") ?? String(providers, "MusicBrainzRecording") : null;
            return new(String(element, "Name") ?? "", Refs(element, "ArtistItems"), Refs(element, "AlbumArtists"), String(element, "AlbumId"),
                String(element, "Album"), String(element, "AlbumPrimaryImageTag"), imageTag, Int(element, "ParentIndexNumber"),
                Int(element, "IndexNumber"), ticks / TimeSpan.TicksPerMillisecond, Int(element, "ProductionYear"), ReadFormat(element), gain,
                musicBrainz, String(element, "DateModified"));
        }

        private static V2Format? ReadFormat(JsonElement element)
        {
            if (!element.TryGetProperty("MediaSources", out var sources) || sources.ValueKind != JsonValueKind.Array) return null;
            foreach (var source in sources.EnumerateArray())
            {
                var stream = source.TryGetProperty("MediaStreams", out var streams) && streams.ValueKind == JsonValueKind.Array
                    ? streams.EnumerateArray().FirstOrDefault(item => String(item, "Type") == "Audio") : default;
                var hasStream = stream.ValueKind == JsonValueKind.Object;
                return FormatOf(String(source, "Container")?.ToLowerInvariant(), hasStream ? String(stream, "Codec") : null,
                    Int(source, "Bitrate") ?? (hasStream ? Int(stream, "BitRate") : null), hasStream ? Int(stream, "SampleRate") : null,
                    hasStream ? Int(stream, "BitDepth") : null, hasStream ? Int(stream, "Channels") : null);
            }
            return null;
        }
    }
}
