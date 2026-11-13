using allstarr.Core.ClientApi;
using allstarr.Core.Enrichment;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Services.Local;

namespace allstarr.Core.ClientApiV2;

/// <summary>Listener library resources shared by their routes and batched reads.</summary>
public sealed class V2LibraryEndpoints(MixReader mixes, ReleaseReader releases, V2TrackReader tracks,
    ClientLibraryAdditions additions, IKaraokeModelService karaoke, ProblemReportService reports)
{
    public Task<V2Result> MixesAsync(HttpContext http, ProtocolExecutionContext execution, string? timeZoneId) => ExecuteAsync(async () =>
    {
        var result = await mixes.ListAsync(http, execution, timeZoneId);
        return new(200, new V2Mixes(result.Mixes.Select(mix => new V2Mix(V2Ids.Mix(mix.MixId), mix.Kind, mix.Name,
            mix.Description, mix.State, mix.ItemCount, mix.DurationMs, mix.UpdatedAt,
            $"/allstarr/v2/mixes/{V2Ids.Mix(mix.MixId)}/cover" + new Uri("http://localhost" + mix.CoverUrl).Query,
            mix.InApps, mix.PlaylistId == null ? null : V2Ids.Playlist(mix.PlaylistId), mix.Section,
            mix.Source, mix.SourcePlaylistId, mix.AppsUnavailableReason)).ToArray()));
    });

    public Task<V2Result> MixTracksAsync(HttpContext http, ProtocolExecutionContext execution, string id) => ExecuteAsync(async () =>
    {
        if (!TryMixId(id, out var mixId)) return new(400, null);
        var mix = await mixes.ItemsAsync(http, execution, mixId);
        var result = await tracks.ReadAsync(http, execution, mix.Items.Select(item => item.ItemId).ToArray(), false);
        return new(200, new V2MixTracks(V2Ids.Mix(mixId), mix.UpdatedAt, result.Tracks, result.Missing));
    });

    public Task<V2Result> ReleasesAsync(HttpContext http, ProtocolExecutionContext execution) => ExecuteAsync(async () =>
    {
        var result = await releases.ListAsync(http, execution);
        return new(200, new V2Releases(result.Releases.Select(release => new V2Release(release.ReleaseId,
            release.Title, release.Kind, release.ReleaseDate, Artist(release.Artist), V2Ids.Album(release.AlbumId),
            $"/allstarr/v2/releases/{Uri.EscapeDataString(release.ReleaseId)}/cover")).ToArray(),
            result.FollowedArtists.Select(Artist).ToArray()));
    });

    public Task<V2Result> FollowAsync(HttpContext http, ProtocolExecutionContext execution, string id, bool followed) => ExecuteAsync(async () =>
    {
        if (!V2Ids.TryDecode(id, V2IdKind.Artist, out var artist)) return new(400, null);
        var result = await releases.FollowAsync(http, execution, artist.Source!, followed);
        return result == null ? new(404, null) : new(200, new V2ArtistFollow(result.Followed, Artist(result.Artist)));
    });

    public Task<V2Result> AdditionsAsync(ProtocolExecutionContext execution) => ExecuteAsync(async () =>
        new(200, (await additions.ListAsync(execution, execution.CancellationToken)).Select(Addition).ToArray()));

    public Task<V2Result> AddAsync(ProtocolExecutionContext execution, V2AddLibraryItem request, string? key) => ExecuteAsync(async () =>
    {
        if (request.Unit is not ("song" or "album") || !TryItem(request.ItemId, request.Unit == "album", out var item)) return new(400, null);
        var result = await additions.AddIdempotentAsync(execution, new(item!, request.Unit), key, execution.CancellationToken);
        return new(result.Replayed ? 200 : 202, Addition(result.Addition));
    });

    public Task<V2Result> CancelAsync(ProtocolExecutionContext execution, string id) => ExecuteAsync(async () =>
    {
        if (!Guid.TryParseExact(id, "D", out var parsed) && !Guid.TryParseExact(id, "N", out parsed)) return new(400, null);
        return new(200, Addition(await additions.CancelAsync(execution, parsed, execution.CancellationToken)));
    });

    public Task<V2Result> ReportAsync(ProtocolExecutionContext execution, V2CreateReport request) => ExecuteAsync(async () =>
    {
        string? item = null;
        if (request.ItemId != null && !TryItem(request.ItemId, false, out item)) return new(400, null);
        var result = await reports.CreateListenerAsync(execution, new(request.Kind, request.Note, item, request.PlaybackRequestId), execution.CancellationToken);
        return new(201, new V2ReportReceipt(result.Id));
    });

    public V2Result Karaoke()
    {
        var model = karaoke.GetCurrentModelMetadata();
        return model == null ? new(404, null) : new(200, new V2KaraokeModel(model.Version, model.Sha256, model.SizeBytes,
            model.ComputeUnits, model.Format, $"/allstarr/v2/models/karaoke/{Uri.EscapeDataString(model.Version)}/file", model.License));
    }

    internal static bool TryMixId(string id, out Guid mix)
    {
        mix = default;
        return V2Ids.TryDecode(id, V2IdKind.Mix, out var decoded) &&
            (Guid.TryParseExact(decoded.Source, "D", out mix) || Guid.TryParseExact(decoded.Source, "N", out mix));
    }

    internal static bool IsReleaseId(string id) => id.Length == 64 && id.All(char.IsAsciiHexDigit);

    private static bool TryItem(string id, bool album, out string? source)
    {
        source = null;
        if (!V2Ids.TryDecode(id, V2IdKind.Track, out var decoded) &&
            !(album && V2Ids.TryDecode(id, V2IdKind.Album, out decoded))) return false;
        source = decoded.Recording is { } recording ? CanonicalRecordingRecord.DefaultPublicId(recording) : decoded.Source;
        return true;
    }

    private static V2ReleaseArtist Artist(ClientReleaseArtist artist) =>
        new(artist.ArtistId == null ? null : V2Ids.Artist(artist.ArtistId), artist.Name, artist.ImageUrl);

    private static string TrackId(string id) => id.StartsWith("ext-allstarr-song-", StringComparison.Ordinal) &&
        Guid.TryParse(id["ext-allstarr-song-".Length..], out var recording) ? V2Ids.Track(recording) : V2Ids.TrackSource(id);

    private static V2LibraryAddition Addition(ClientLibraryAddition addition) => new(addition.Id, TrackId(addition.ItemId),
        addition.Unit, addition.State, addition.Reason, addition.LibraryItemId,
        addition.RequestedItemId is not { } requested ? null : LocalLibraryService.ParseExternalResource(requested).type == "album"
            ? V2Ids.Album(requested) : TrackId(requested));

    private static async Task<V2Result> ExecuteAsync(Func<Task<V2Result>> action)
    {
        try { return await action(); }
        catch (UnauthorizedAccessException) { return new(404, null); }
        catch (KeyNotFoundException) { return new(404, null); }
        catch (ArgumentException) { return new(400, null); }
        catch (PromotionException error) { return new(error.Status, new ClientApiError(ClientApiError.ForStatus(error.Status).Code, error.Message)); }
        catch (ProblemReportException error) { return new(error.StatusCode, null); }
        catch (HttpRequestException) { return new(503, null); }
    }
}
