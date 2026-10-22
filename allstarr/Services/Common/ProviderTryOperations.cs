using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Spotify;

namespace allstarr.Services.Common;

public sealed class ProviderTryOperations(IProviderRegistry providers)
{
    private readonly ProviderDiagnosticSamples _diagnosticSamples = new();

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
                    if (Has("fetchLyrics")) operations.Add(new("lyrics", ["title", "artist", "album", "durationSeconds", "isrc"]));
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
        descriptor.Capabilities.Single(item => item.Capability == capability);
        if (descriptor.HealthProbe && descriptor.Capabilities.Any(item => item.Capability == ProviderCapabilityKind.Health &&
            item.Hooks.Contains("probe" + capability)) && providers.TryGetCapability<IProviderHealthProbeCapability>(descriptor.Id,
            ProviderCapabilityKind.Health, out var health))
        {
            var probe = await health!.ProbeAsync(context, new(capability));
            if (!probe.IsSuccess) return Failure(probe.Error!);
            var healthResult = probe.RequireValue();
            return healthResult.Status == ProviderProbeStatus.Healthy ? new() { Succeeded = true } :
                ProviderTryResult.Failure(healthResult.SafeCode ?? (healthResult.Status == ProviderProbeStatus.Unauthorized ? "unauthorized" : "service_unavailable"));
        }
        bool Has(ProviderCapabilityKind kind, string hook) => descriptor.Capabilities.Any(item => item.Capability == kind &&
            item.HasUsableImplementation && item.Hooks.Contains(hook));
        if (capability == ProviderCapabilityKind.Playlist && context.Account != null && Has(capability, "getUserPlaylists"))
            return await ExecuteAsync(context, new() { Capability = "playlist", Operation = "mine" });
        if (capability is not (ProviderCapabilityKind.Metadata or ProviderCapabilityKind.Lyrics or ProviderCapabilityKind.Download or ProviderCapabilityKind.Streaming) ||
            capability == ProviderCapabilityKind.Metadata && !Has(capability, "searchTracks") ||
            capability == ProviderCapabilityKind.Download && !Has(capability, "checkAvailability"))
            return ProviderTryResult.Failure("probe_not_available");
        ProviderTryResult? last = null;
        var triedIds = new HashSet<string>(StringComparer.Ordinal);
        var result = await _diagnosticSamples.CheckAsync($"{descriptor.Id}:{context.Account?.AccountId}:{capability}",
            ProviderDiagnosticSamples.Recordings(descriptor.Id), async (recording, token) =>
            {
                var execution = ProviderDiagnosticSamples.WithCancellation(context, token);
                try { last = await ProbeSampleAsync(execution, descriptor, capability, recording, triedIds); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception) { last = ProviderTryResult.Failure("provider_error"); }
                return new ProviderSampleCheck(last.Succeeded || last.ReasonCode == "download-not-verified", last.ReasonCode,
                    last.ReasonCode is "not_found" or "not-found" or "incompatible-media" or "sample-unavailable");
            }, DiscoverAsync, context.CancellationToken);
        return result.Success && last != null ? last : ProviderTryResult.Failure(result.SampleUnavailable
            ? "sample-unavailable" : result.ReasonCode ?? "sample-unavailable");

        async IAsyncEnumerable<ProviderDiagnosticSample> DiscoverAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            await Task.CompletedTask;
            foreach (var query in ProviderDiagnosticSamples.DiscoveryQueries(descriptor.Id))
            {
                token.ThrowIfCancellationRequested();
                yield return new(query, "", null, 200);
            }
        }
    }

    private async Task<ProviderTryResult> ProbeSampleAsync(ProviderExecutionContext context, ProviderDescriptor descriptor,
        ProviderCapabilityKind capability, ProviderDiagnosticSample recording, HashSet<string> triedIds)
    {
        bool Has(ProviderCapabilityKind kind, string hook) => descriptor.Capabilities.Any(item => item.Capability == kind &&
            item.HasUsableImplementation && item.Hooks.Contains(hook));
        ProviderTrackMetadata? sample = null;
        if (Has(ProviderCapabilityKind.Metadata, "searchTracks"))
        {
            var outcome = await providers.GetRequiredCapability<IProviderMetadataCapability>(descriptor.Id, ProviderCapabilityKind.Metadata)
                .SearchTracksAsync(context, new(recording.Query.Trim(), new(3))).WaitAsync(context.CancellationToken);
            if (!outcome.IsSuccess) return Failure(outcome.Error!);
            sample = outcome.RequireValue().Items.FirstOrDefault();
            if (sample == null) return ProviderTryResult.Failure("not_found");
        }
        if (capability == ProviderCapabilityKind.Metadata) return new() { Succeeded = sample != null };
        if (sample != null && !triedIds.Add(sample.Id.Value)) return ProviderTryResult.Failure("not_found");
        if (capability == ProviderCapabilityKind.Streaming)
        {
            if (sample == null || !Has(capability, "probeStream")) return ProviderTryResult.Failure("probe_not_available");
            var probe = await providers.GetRequiredCapability<IProviderStreamingCapability>(descriptor.Id, capability)
                .ProbeStreamAsync(context, new(sample.Id)).WaitAsync(context.CancellationToken);
            return !probe.IsSuccess ? Failure(probe.Error!) : probe.RequireValue().Available
                ? new() { Succeeded = true } : ProviderTryResult.Failure("not_found");
        }
        if (capability == ProviderCapabilityKind.Lyrics)
            return await ExecuteAsync(context, new()
            {
                Capability = "lyrics",
                Operation = "lyrics",
                TrackId = sample?.Id.Value,
                Title = sample?.Title ?? recording.Title,
                Artist = sample == null ? recording.Artist : string.Join(",", sample.Artists.Select(item => item.Name)),
                Album = sample?.AlbumTitle,
                DurationSeconds = (int?)sample?.Duration?.TotalSeconds ?? recording.DurationSeconds,
                Isrc = sample?.Isrc ?? recording.Isrc
            });
        if (sample == null) return ProviderTryResult.Failure("not_found");
        var available = await providers.GetRequiredCapability<IProviderDownloadCapability>(descriptor.Id, capability)
            .CheckAvailabilityAsync(context, new(sample.Id)).WaitAsync(context.CancellationToken);
        return available.IsSuccess ? available.RequireValue().State == ProviderDownloadAvailabilityState.Available
            ? ProviderTryResult.Failure("download-not-verified") : ProviderTryResult.Failure("not_found") : Failure(available.Error!);
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
    private static string? Artwork(ProviderArtworkReference? artwork, string? provider = null) => artwork?.PublicUri is { } uri &&
        string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.UserInfo) ? uri.AbsoluteUri :
            provider != null ? ProviderArtworkFallback.CoverPath(provider) : null;
    private static object Track(ProviderTrackMetadata track) => new
    {
        id = track.Id.Value,
        title = track.Title,
        isBestVersion = track.IsBestVersion,
        artists = track.Artists.Select(item => item.Name).ToArray(),
        album = track.AlbumTitle,
        durationSeconds = track.Duration?.TotalSeconds,
        isrc = track.Isrc,
        @explicit = track.IsExplicit,
        artworkUrl = Artwork(track.Artwork, track.Id.ProviderId),
        year = track.Year
    };
    private static object Album(ProviderAlbumMetadata album) => new
    {
        id = album.Id.Value,
        title = album.Title,
        artists = album.Artists.Select(item => item.Name).ToArray(),
        year = album.Year,
        trackCount = album.TrackCount,
        artworkUrl = Artwork(album.Artwork, album.Id.ProviderId)
    };
    private static object Artist(ProviderArtistMetadata artist) => new { id = artist.Id.Value, name = artist.Name, artworkUrl = Artwork(artist.Artwork, artist.Id.ProviderId) };
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
