using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Capabilities;

namespace allstarr.Core.Playlists.Sources;

public enum PlaylistSnapshotCollectionStatus
{
    Fresh,
    LastKnownGood,
    Failed
}

public sealed record CollectedPlaylistSourceEntry(
    int SourcePosition,
    string SourceEntryIdHash,
    string ProviderTrackIdHash,
    Guid? CanonicalRecordingId,
    string? Title,
    IReadOnlyList<string> Artists,
    string? Album,
    long? DurationMilliseconds,
    string? Isrc,
    bool? IsExplicit,
    string? ArtworkUrl = null);

public sealed record CollectedPlaylistSourceSnapshot(
    string ProviderId,
    Guid ProviderAccountId,
    string PlaylistIdHash,
    string SourceRevision,
    string? SourceETag,
    string Name,
    string? Description,
    string? ArtworkReferenceKey,
    IReadOnlyList<CollectedPlaylistSourceEntry> Entries);

public sealed record PlaylistSnapshotCollectionResult(
    PlaylistSnapshotCollectionStatus Status,
    CollectedPlaylistSourceSnapshot? Snapshot,
    ProviderError? Error,
    int PagesRead)
{
    public bool IsSuccess => Status is PlaylistSnapshotCollectionStatus.Fresh or PlaylistSnapshotCollectionStatus.LastKnownGood;
}

public sealed record ProviderPlaylistSnapshotRequest(
    ProviderExternalResourceId PlaylistId,
    int PageSize = 100,
    string? ExpectedSourceRevision = null,
    CollectedPlaylistSourceSnapshot? LastKnownGood = null);

public sealed class ProviderPlaylistSnapshotCollector
{
    private const int MaximumPages = 1_000;
    private const int MaximumEntries = 100_000;

    public async Task<PlaylistSnapshotCollectionResult> CollectAsync(
        IProviderPlaylistCapability capability,
        ProviderExecutionContext context,
        ProviderPlaylistSnapshotRequest request)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        if (context.Account == null)
            throw new InvalidOperationException("Playlist snapshot collection requires one explicit provider account.");
        if (!context.ProviderId.Equals(capability.ProviderId, StringComparison.Ordinal) ||
            !context.Account.ProviderId.Equals(capability.ProviderId, StringComparison.Ordinal))
            throw new ArgumentException("The capability, execution context, and selected account must belong to the same provider.");
        context.RequireResourceOwner(request.PlaylistId, ProviderResourceKind.Playlist);
        if (request.PageSize is < 1 or > 200)
            throw new ArgumentOutOfRangeException(nameof(request), "Page size must be between 1 and 200.");

        var playlistIdHash = HashResource(request.PlaylistId);
        ValidateLastKnownGood(request.LastKnownGood, context, playlistIdHash);
        var entries = new List<CollectedPlaylistSourceEntry>();
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        ProviderPlaylistSummary? summary = null;
        string? pageSnapshotVersion = null;
        string? cursor = null;
        var pagesRead = 0;
        try
        {
            do
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                if (++pagesRead > MaximumPages)
                    return Failure(new ProviderError(ProviderErrorKind.PermanentFailure), request.LastKnownGood, pagesRead, context, playlistIdHash);
                var outcome = await capability.GetPlaylistTracksAsync(
                    context,
                    new ProviderPlaylistTracksRequest(
                        request.PlaylistId,
                        new ProviderPageRequest(request.PageSize, cursor),
                        summary?.SourceRevision ?? request.ExpectedSourceRevision));
                if (!outcome.IsSuccess)
                    return Failure(outcome.Error!, request.LastKnownGood, pagesRead, context, playlistIdHash);

                var page = outcome.RequireValue();
                if (page.Playlist.TrackCount > MaximumEntries ||
                    page.Tracks.Items.Count > MaximumEntries - entries.Count)
                    return Failure(new ProviderError(ProviderErrorKind.PermanentFailure), request.LastKnownGood, pagesRead, context, playlistIdHash);
                ValidatePage(capability.ProviderId, request.PlaylistId, page, summary, pageSnapshotVersion, entries, request.PageSize);
                if (summary == null && request.ExpectedSourceRevision != null &&
                    request.ExpectedSourceRevision != page.Playlist.SourceRevision)
                    throw new InvalidProviderPageException();
                summary ??= page.Playlist;
                pageSnapshotVersion ??= page.Tracks.SnapshotVersion;
                foreach (var track in page.Tracks.Items)
                {
                    entries.Add(ToCollectedEntry(request.PlaylistId, summary.SourceRevision, track));
                }

                cursor = page.Tracks.NextCursor;
                if (cursor != null && !seenCursors.Add(cursor))
                    return Failure(new ProviderError(ProviderErrorKind.PermanentFailure), request.LastKnownGood, pagesRead, context, playlistIdHash);
            } while (cursor != null);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            return new(PlaylistSnapshotCollectionStatus.Failed, null, new ProviderError(ProviderErrorKind.Canceled), pagesRead);
        }
        catch (InvalidProviderPageException)
        {
            return Failure(new ProviderError(ProviderErrorKind.PermanentFailure), request.LastKnownGood, pagesRead, context, playlistIdHash);
        }
        catch (Exception) when (!context.CancellationToken.IsCancellationRequested)
        {
            return Failure(new ProviderError(ProviderErrorKind.TransientFailure), request.LastKnownGood, pagesRead, context, playlistIdHash);
        }

        if (summary == null)
            return Failure(new ProviderError(ProviderErrorKind.PermanentFailure), request.LastKnownGood, pagesRead, context, playlistIdHash);
        if (entries.Count != summary.TrackCount ||
            entries.Select(item => item.SourcePosition).Distinct().Count() != entries.Count ||
            entries.Count > 1 && entries.Zip(entries.Skip(1), (left, right) => right.SourcePosition == left.SourcePosition + 1).Any(contiguous => !contiguous))
            return Failure(new ProviderError(ProviderErrorKind.PermanentFailure), request.LastKnownGood, pagesRead, context, playlistIdHash);
        var artwork = StableArtworkReference(summary.Artwork);
        var snapshot = new CollectedPlaylistSourceSnapshot(
            capability.ProviderId,
            context.Account.AccountId,
            playlistIdHash,
            summary.SourceRevision,
            summary.SourceETag,
            summary.Name,
            summary.Description,
            artwork,
            entries.ToArray());
        return new(PlaylistSnapshotCollectionStatus.Fresh, snapshot, null, pagesRead);
    }

    private static void ValidatePage(
        string providerId,
        ProviderExternalResourceId requestedPlaylist,
        ProviderPlaylistTrackPage page,
        ProviderPlaylistSummary? firstSummary,
        string? firstPageVersion,
        IReadOnlyList<CollectedPlaylistSourceEntry> collected,
        int requestedPageSize)
    {
        if (!page.Playlist.Id.Equals(requestedPlaylist) ||
            !page.Tracks.ProviderId.Equals(providerId, StringComparison.Ordinal) ||
            page.Tracks.Items.Count > requestedPageSize)
            throw new InvalidProviderPageException();
        if (firstSummary != null &&
            (firstSummary.SourceRevision != page.Playlist.SourceRevision ||
             firstSummary.SourceETag != page.Playlist.SourceETag ||
             firstSummary.Name != page.Playlist.Name ||
             firstSummary.Description != page.Playlist.Description))
            throw new InvalidProviderPageException();
        if (firstSummary != null && page.Tracks.SnapshotVersion != firstPageVersion)
            throw new InvalidProviderPageException();
        var positions = page.Tracks.Items.Select(track => track.Position).ToArray();
        if (!positions.SequenceEqual(positions.Order()) ||
            positions.Distinct().Count() != positions.Length ||
            positions.Length > 1 && positions.Zip(positions.Skip(1), (left, right) => right == left + 1).Any(contiguous => !contiguous))
            throw new InvalidProviderPageException();
        if (positions.Length > 0 && collected.Count > 0 && positions[0] != collected[^1].SourcePosition + 1)
            throw new InvalidProviderPageException();
        if (page.Tracks.IsPartial && page.Tracks.NextCursor == null)
            throw new InvalidProviderPageException();
    }

    private static CollectedPlaylistSourceEntry ToCollectedEntry(
        ProviderExternalResourceId playlistId,
        string revision,
        ProviderPlaylistTrack track)
    {
        var metadata = track.Metadata;
        var sourceEntryHash = HashText($"{HashResource(playlistId)}\u001f{revision}\u001f{track.Position}\u001f{HashResource(track.TrackId)}");
        return new(
            track.Position,
            sourceEntryHash,
            HashResource(track.TrackId),
            track.CanonicalRecordingId,
            metadata?.Title,
            metadata?.Artists.Select(artist => artist.Name).ToArray() ?? [],
            metadata?.AlbumTitle,
            metadata?.Duration is { } duration
                ? checked((long)Math.Round(duration.TotalMilliseconds))
                : null,
            metadata?.Isrc,
            metadata?.IsExplicit,
            metadata?.Artwork?.PublicUri?.AbsoluteUri);
    }

    private static string? StableArtworkReference(ProviderArtworkReference? artwork)
    {
        if (artwork?.ResourceId != null)
            return $"provider-artwork:{HashResource(artwork.ResourceId)}:{HashText(artwork.Revision ?? "unversioned")}";
        if (artwork?.PublicUri is { Query.Length: 0, Fragment.Length: 0 } uri)
            return $"provider-artwork-url:{HashText(uri.AbsoluteUri)}:{HashText(artwork.Revision ?? "unversioned")}";
        return null;
    }

    public static string HashResource(ProviderExternalResourceId resource) =>
        HashText($"{resource.ProviderId}\u001f{resource.ResourceKind}\u001f{resource.Catalog ?? "default"}\u001f{resource.Value}");

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static PlaylistSnapshotCollectionResult Failure(
        ProviderError error,
        CollectedPlaylistSourceSnapshot? lastKnownGood,
        int pagesRead,
        ProviderExecutionContext context,
        string playlistIdHash)
    {
        if (lastKnownGood != null && IsRetryable(error.Kind) &&
            lastKnownGood.ProviderId == context.ProviderId &&
            lastKnownGood.ProviderAccountId == context.Account!.AccountId &&
            lastKnownGood.PlaylistIdHash == playlistIdHash)
            return new(PlaylistSnapshotCollectionStatus.LastKnownGood, lastKnownGood, error, pagesRead);
        return new(PlaylistSnapshotCollectionStatus.Failed, null, error, pagesRead);
    }

    private static bool IsRetryable(ProviderErrorKind kind) => kind is
        ProviderErrorKind.TransientFailure or
        ProviderErrorKind.RateLimited or
        ProviderErrorKind.CapabilityUnavailable;

    private static void ValidateLastKnownGood(
        CollectedPlaylistSourceSnapshot? snapshot,
        ProviderExecutionContext context,
        string playlistIdHash)
    {
        if (snapshot == null) return;
        if (snapshot.ProviderId != context.ProviderId ||
            snapshot.ProviderAccountId != context.Account!.AccountId ||
            snapshot.PlaylistIdHash != playlistIdHash)
            throw new ArgumentException("The last-known-good snapshot belongs to another provider account or playlist.");
    }

    private sealed class InvalidProviderPageException : Exception { }
}
