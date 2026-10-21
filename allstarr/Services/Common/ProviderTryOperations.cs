using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Spotify;

namespace allstarr.Services.Common;

public sealed class ProviderTryOperations(IProviderRegistry providers)
{
    public static ProviderTryCatalog Describe(ProviderDescriptor provider, Guid? accountId)
    {
        var capabilities = new List<ProviderTryCapability>();
        foreach (var capability in provider.Capabilities.Where(item => item.HasUsableImplementation))
        {
            var operations = new List<ProviderTryOperation>();
            bool Has(string hook) => capability.Hooks.Contains(hook, StringComparer.Ordinal);
            switch (capability.Capability)
            {
                case ProviderCapabilityKind.Metadata:
                    var kinds = new List<string>();
                    if (Has("searchTracks")) kinds.Add("tracks");
                    if (Has("searchAlbums")) kinds.Add("albums");
                    if (Has("searchArtists")) kinds.Add("artists");
                    if (kinds.Count > 0) operations.Add(new("search", ["query", "kind"], kinds));
                    if (Has("getTrack")) operations.Add(new("track", ["id"]));
                    if (Has("lookupByIsrc")) operations.Add(new("isrc", ["isrc"]));
                    break;
                case ProviderCapabilityKind.Lyrics:
                    if (Has("fetchLyrics")) operations.Add(new("lyrics", ["title", "artist", "album", "durationSeconds", "isrc", "trackId"]));
                    break;
                case ProviderCapabilityKind.Playlist:
                    if (Has("getPlaylistTracks")) operations.Add(new("playlist", ["url"]));
                    if (Has("getUserPlaylists") && (accountId.HasValue && accountId != Guid.Empty ||
                        capability.AccountRequirement == ProviderAccountRequirement.Required)) operations.Add(new("mine", []));
                    break;
                case ProviderCapabilityKind.Streaming:
                    if (Has("getStreamLease")) operations.Add(new("stream", ["trackId", "quality"]));
                    break;
                case ProviderCapabilityKind.Download:
                    if (Has("download")) operations.Add(new("download", ["trackId", "quality"]));
                    break;
            }
            if (operations.Count > 0) capabilities.Add(new(capability.Capability.ToString().ToLowerInvariant(),
                capability.AccountRequirement.ToString().ToLowerInvariant(), operations));
        }
        return new(provider.Id, capabilities);
    }

    public async Task<ProviderTryResult> ExecuteAsync(ProviderExecutionContext context, ProviderTryRequest request)
    {
        try
        {
            var result = await ExecuteCoreAsync(context, request).WaitAsync(context.CancellationToken);
            return context.CancellationToken.IsCancellationRequested ? ProviderTryResult.Failure("timeout") : result;
        }
        catch (OperationCanceledException) { return ProviderTryResult.Failure("timeout"); }
        catch (Exception) { return ProviderTryResult.Failure("provider_error"); }
    }

    private async Task<ProviderTryResult> ExecuteCoreAsync(ProviderExecutionContext context, ProviderTryRequest request)
    {
        var provider = context.ProviderId;
        ProviderExternalResourceId TrackId(string id) => new(provider, ProviderResourceKind.Track, id.Trim());
        if (request.Capability == "metadata")
        {
            var metadata = providers.GetRequiredCapability<IProviderMetadataCapability>(provider, ProviderCapabilityKind.Metadata);
            var search = request.Operation == "search" ? new ProviderMetadataSearchRequest(request.Query!.Trim(), new(20)) : null;
            if (request.Operation == "search" && (request.Kind ?? "tracks") == "albums")
            {
                var outcome = await metadata.SearchAlbumsAsync(context, search!);
                if (!outcome.IsSuccess) return Failure(outcome.Error!);
                return new()
                {
                    Succeeded = outcome.RequireValue().Items.Count > 0,
                    ReasonCode = outcome.RequireValue().Items.Count == 0 ? "not_found" : null,
                    Albums = outcome.RequireValue().Items.Take(20).Select(Album).ToArray()
                };
            }
            if (request.Operation == "search" && request.Kind == "artists")
            {
                var outcome = await metadata.SearchArtistsAsync(context, search!);
                if (!outcome.IsSuccess) return Failure(outcome.Error!);
                return new()
                {
                    Succeeded = outcome.RequireValue().Items.Count > 0,
                    ReasonCode = outcome.RequireValue().Items.Count == 0 ? "not_found" : null,
                    Artists = outcome.RequireValue().Items.Take(20).Select(Artist).ToArray()
                };
            }
            if (request.Operation == "search")
            {
                var outcome = await metadata.SearchTracksAsync(context, search!);
                if (!outcome.IsSuccess) return Failure(outcome.Error!);
                return Tracks(outcome.RequireValue().Items.Take(20));
            }
            var track = request.Operation == "track"
                ? await metadata.GetTrackAsync(context, new(TrackId(request.Id!)))
                : await metadata.LookupByIsrcAsync(context, new(request.Isrc!.Trim()));
            return track.IsSuccess ? Tracks([track.RequireValue()]) : Failure(track.Error!);
        }
        if (request.Capability == "lyrics")
        {
            var lyrics = providers.GetRequiredCapability<IProviderLyricsCapability>(provider, ProviderCapabilityKind.Lyrics);
            var outcome = await lyrics.FetchLyricsAsync(context, new(Guid.NewGuid(), TrackId(string.IsNullOrWhiteSpace(request.TrackId) ? "lyrics-lookup" : request.TrackId),
                trackTitle: request.Title?.Trim(), artistNames: request.Artist?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                albumTitle: request.Album?.Trim(), durationSeconds: request.DurationSeconds, isrc: request.Isrc?.Trim()));
            if (!outcome.IsSuccess) return Failure(outcome.Error!);
            var value = outcome.RequireValue();
            if (value.Availability == ProviderLyricsAvailabilityState.Restricted)
                return ProviderTryResult.Failure("lyrics-restricted", "The provider has restricted these lyrics; they are unavailable for display.");
            if (value.Availability != ProviderLyricsAvailabilityState.Available) return ProviderTryResult.Failure("not_found");
            var lines = value.TimedLines?.Select(line => new ProviderTryLyricLine(line.Start.TotalMilliseconds, CleanLyric(line.Text))).ToArray()
                ?? ParseLyrics(value.Content ?? "");
            return new()
            {
                Succeeded = true,
                Lyrics = new
                {
                    format = value.Format == ProviderLyricsFormat.WordTimed ? "word" : value.Format == ProviderLyricsFormat.LineTimed ? "line" : "plain",
                    source = value.Source,
                    language = (string?)null,
                    lineCount = lines.Length,
                    lines
                }
            };
        }
        if (request.Capability == "playlist")
        {
            var playlists = providers.GetRequiredCapability<IProviderPlaylistCapability>(provider, ProviderCapabilityKind.Playlist);
            if (request.Operation == "mine")
            {
                var outcome = await playlists.GetUserPlaylistsAsync(context, new(new(100)));
                return outcome.IsSuccess ? new() { Succeeded = true, Playlists = outcome.RequireValue().Items.Take(100).Select(Playlist).ToArray() } : Failure(outcome.Error!);
            }
            var id = PlaylistId(provider, request.Url!.Trim());
            var result = await playlists.GetPlaylistTracksAsync(context, new(new(provider, ProviderResourceKind.Playlist, id), new(100)));
            if (!result.IsSuccess) return Failure(result.Error!);
            return new()
            {
                Succeeded = true,
                Playlist = Playlist(result.RequireValue().Playlist),
                Tracks = result.RequireValue().Tracks.Items.Take(100).Select(item => item.Metadata != null ? Track(item.Metadata) :
                    (object)new
                    {
                        id = item.TrackId.Value,
                        title = "Unknown track",
                        artists = Array.Empty<string>(),
                        album = (string?)null,
                        durationSeconds = (double?)null,
                        isrc = (string?)null,
                        @explicit = (bool?)null,
                        artworkUrl = (string?)null,
                        year = (int?)null
                    }).ToArray()
            };
        }
        return ProviderTryResult.Failure("not_supported");
    }

    public async Task<ProviderTryResult> ProbeAsync(ProviderExecutionContext context, ProviderDescriptor descriptor, ProviderCapabilityKind capability)
    {
        var selected = descriptor.Capabilities.Single(item => item.Capability == capability);
        bool Has(ProviderCapabilityKind kind, string hook) => descriptor.Capabilities.Any(item => item.Capability == kind &&
            item.HasUsableImplementation && item.Hooks.Contains(hook));
        if (capability == ProviderCapabilityKind.Metadata)
            return Has(capability, "searchTracks") ? await ExecuteAsync(context,
                new() { Capability = "metadata", Operation = "search", Query = "Daft Punk Get Lucky", Kind = "tracks" }) :
                ProviderTryResult.Failure("not_supported");
        if (capability == ProviderCapabilityKind.Playlist && context.Account != null && Has(capability, "getUserPlaylists"))
            return await ExecuteAsync(context, new() { Capability = "playlist", Operation = "mine" });
        if (capability is not (ProviderCapabilityKind.Lyrics or ProviderCapabilityKind.Download))
            return new() { Succeeded = selected.HasUsableImplementation };
        if (capability == ProviderCapabilityKind.Download && !Has(capability, "checkAvailability"))
            return new() { Succeeded = selected.HasUsableImplementation };
        try
        {
            ProviderTrackMetadata? sample = null;
            if (Has(ProviderCapabilityKind.Metadata, "searchTracks"))
            {
                var outcome = await providers.GetRequiredCapability<IProviderMetadataCapability>(descriptor.Id, ProviderCapabilityKind.Metadata)
                    .SearchTracksAsync(context, new("Daft Punk Get Lucky", new(20))).WaitAsync(context.CancellationToken);
                if (!outcome.IsSuccess) return Failure(outcome.Error!);
                sample = outcome.RequireValue().Items.FirstOrDefault();
                if (sample == null) return ProviderTryResult.Failure("not_found");
            }
            if (capability == ProviderCapabilityKind.Lyrics)
                return await ExecuteAsync(context, new()
                {
                    Capability = "lyrics",
                    Operation = "lyrics",
                    TrackId = sample?.Id.Value,
                    Title = sample?.Title ?? "Get Lucky",
                    Artist = sample == null ? "Daft Punk" : string.Join(",", sample.Artists.Select(item => item.Name)),
                    Album = sample?.AlbumTitle,
                    DurationSeconds = (int?)sample?.Duration?.TotalSeconds ?? 369,
                    Isrc = sample?.Isrc ?? "USQX91300108"
                });
            if (sample == null) return ProviderTryResult.Failure("not_found");
            var available = await providers.GetRequiredCapability<IProviderDownloadCapability>(descriptor.Id, capability)
                .CheckAvailabilityAsync(context, new(sample.Id)).WaitAsync(context.CancellationToken);
            return available.IsSuccess ? available.RequireValue().State == ProviderDownloadAvailabilityState.Available
                ? new() { Succeeded = true } : ProviderTryResult.Failure("not_found") : Failure(available.Error!);
        }
        catch (OperationCanceledException) { return ProviderTryResult.Failure("timeout"); }
        catch (Exception) { return ProviderTryResult.Failure("provider_error"); }
    }

    public static ProviderAudioQuality Quality(string? quality) => quality switch
    {
        "lossy" => ProviderAudioQuality.Lossy,
        "lossless" => ProviderAudioQuality.Lossless,
        "high-resolution" => ProviderAudioQuality.HighResolution,
        _ => ProviderAudioQuality.Any
    };
    public static ProviderTryResult Failure(ProviderError error) => ProviderTryResult.Failure(
        error.Kind == ProviderErrorKind.NotFound ? "not_found" : error.Code, error.SafeMessage);
    private static ProviderTryResult Tracks(IEnumerable<ProviderTrackMetadata> tracks)
    {
        var items = tracks.Select(Track).ToArray();
        return items.Length == 0 ? ProviderTryResult.Failure("not_found") : new() { Succeeded = true, Tracks = items };
    }
    private static string? Artwork(ProviderArtworkReference? artwork) => artwork?.PublicUri is { } uri &&
        string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.UserInfo) ? uri.AbsoluteUri : null;
    private static object Track(ProviderTrackMetadata track) => new
    {
        id = track.Id.Value,
        title = track.Title,
        artists = track.Artists.Select(item => item.Name).ToArray(),
        album = track.AlbumTitle,
        durationSeconds = track.Duration?.TotalSeconds,
        isrc = track.Isrc,
        @explicit = track.IsExplicit,
        artworkUrl = Artwork(track.Artwork),
        year = track.Year
    };
    private static object Album(ProviderAlbumMetadata album) => new
    {
        id = album.Id.Value,
        title = album.Title,
        artists = album.Artists.Select(item => item.Name).ToArray(),
        year = album.Year,
        trackCount = album.TrackCount,
        artworkUrl = Artwork(album.Artwork)
    };
    private static object Artist(ProviderArtistMetadata artist) => new { id = artist.Id.Value, name = artist.Name, artworkUrl = Artwork(artist.Artwork) };
    private static object Playlist(ProviderPlaylistSummary playlist) => new
    {
        id = playlist.Id.Value,
        title = playlist.Name,
        owner = playlist.Owner.DisplayName,
        trackCount = playlist.TrackCount,
        artworkUrl = Artwork(playlist.Artwork)
    };
    private static string PlaylistId(string provider, string input)
    {
        if (provider == "spotify" && SpotifyPlaylistReference.TryNormalize(input, out var normalized)) return normalized;
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return input;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return Uri.UnescapeDataString(segments.Last());
    }
    private static string CleanLyric(string text) => Regex.Replace(text, @"<\d{1,2}:\d{2}(?:[.:]\d+)?>", "");
    private static ProviderTryLyricLine[] ParseLyrics(string content) => content.Split('\n').Select(line =>
    {
        var match = Regex.Match(line, @"^\[(\d{1,2}):(\d{2})(?:[.:](\d+))?\]");
        double? start = match.Success ? (int.Parse(match.Groups[1].Value) * 60 + int.Parse(match.Groups[2].Value)) * 1000 +
            (match.Groups[3].Success ? double.Parse("0." + match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture) * 1000 : 0) : null;
        return new ProviderTryLyricLine(start, CleanLyric(match.Success ? line[match.Length..].TrimEnd('\r') : line.TrimEnd('\r')));
    }).ToArray();
    private sealed record ProviderTryLyricLine([property: JsonPropertyName("startMs")] double? StartMs,
        [property: JsonPropertyName("text")] string Text);
}
