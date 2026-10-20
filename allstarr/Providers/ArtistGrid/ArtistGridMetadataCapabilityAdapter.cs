using allstarr.Core.Capabilities;

namespace allstarr.Core.Providers.ArtistGrid;

public sealed class ArtistGridMetadataCapabilityAdapter : IProviderMetadataCapability
{
    private readonly ArtistGridCatalog _catalog;

    public ArtistGridMetadataCapabilityAdapter(ArtistGridCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public string ProviderId => ArtistGridConstants.ProviderId;

    public ProviderCapabilityKind Capability => ProviderCapabilityKind.Metadata;

    public async Task<ProviderOutcome<ProviderPage<ProviderTrackMetadata>>> SearchTracksAsync(
        ProviderExecutionContext context,
        ProviderMetadataSearchRequest request)
    {
        var error = ValidateContext(context);
        if (error != null) return Failure<ProviderPage<ProviderTrackMetadata>>(error);

        var tokens = Tokens(request.Query);
        if (tokens.Count == 0)
            return Failure<ProviderPage<ProviderTrackMetadata>>(ProviderErrorKind.NotFound);
        if (!TryOffset(request.Page.Cursor, out var offset))
            return Failure<ProviderPage<ProviderTrackMetadata>>(ProviderErrorKind.PermanentFailure);

        var indexResult = await _catalog.GetArtistIndexAsync(context.CancellationToken).ConfigureAwait(false);
        var matches = indexResult.IsSuccess
            ? FindArtists(indexResult.RequireValue(), tokens)
            : [];
        var candidates = new List<(ArtistGridArtist Artist, ArtistGridTracker Tracker)>();
        var failures = new List<ProviderError>();

        if (matches.Count > 0)
        {
            foreach (var match in matches
                .GroupBy(item => item.Artist.Tracker, StringComparer.Ordinal)
                .Select(group => group.First())
                .Take(3))
            {
                var trackerResult = await _catalog.GetTrackerAsync(
                    match.Artist.Tracker,
                    gid: null,
                    allowNetwork: true,
                    forceRefresh: false,
                    context.CancellationToken).ConfigureAwait(false);
                if (trackerResult.IsSuccess)
                    candidates.Add((match.Artist, trackerResult.RequireValue()));
                else
                    failures.Add(trackerResult.Error!);
            }
        }
        else
        {
            var cached = _catalog.GetCachedTrackers();
            if (!indexResult.IsSuccess && cached.Count == 0)
                return ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Failure(indexResult.Error!);
            candidates.AddRange(cached);
        }

        var queryTitleTokens = matches.Count > 0
            ? FindArtists(indexResult.RequireValue(), tokens).First().RemainingTitleTokens
            : tokens;
        var tracks = new List<ProviderTrackMetadata>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (artist, tracker) in candidates)
        {
            foreach (var rawTrack in tracker.Tracks)
            {
                if (!IsTitleMatch(rawTrack.Title, queryTitleTokens)) continue;
                var source = SelectSource(rawTrack.Sources);
                if (source == null || !IsUsableTrack(rawTrack)) continue;
                var metadata = TryMapTrack(artist, tracker, rawTrack, source);
                if (metadata != null && seenIds.Add(metadata.Id.Value)) tracks.Add(metadata);
            }
        }

        var ordered = tracks
            .OrderBy(item => Normalize(item.Artists.FirstOrDefault()?.Name ?? string.Empty), StringComparer.Ordinal)
            .ThenBy(item => Normalize(item.Title), StringComparer.Ordinal)
            .ThenBy(item => Normalize(item.AlbumTitle ?? string.Empty), StringComparer.Ordinal)
            .ThenBy(item => item.Id.Value, StringComparer.Ordinal)
            .ToArray();
        if (ordered.Length == 0 && failures.Count > 0)
            return ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Failure(failures[0]);
        var pageItems = ordered.Skip(offset).Take(request.Page.Limit).ToArray();
        var next = offset + pageItems.Length < ordered.Length
            ? (offset + pageItems.Length).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
        return ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new(
            ProviderId,
            pageItems,
            next,
            next != null || failures.Count > 0,
            null));
    }

    public async Task<ProviderOutcome<ProviderTrackMetadata>> GetTrackAsync(
        ProviderExecutionContext context,
        ProviderTrackLookupRequest request)
    {
        var error = ValidateContext(context, request.Id, ProviderResourceKind.Track);
        if (error != null) return Failure<ProviderTrackMetadata>(error);
        if (!ArtistGridTrackId.TryParse(request.Id.Value, out var tracker, out var gid, out var sourceHash))
            return Failure<ProviderTrackMetadata>(ProviderErrorKind.NotFound);

        var resolved = await _catalog.ResolveTrackAsync(tracker, gid, sourceHash, context.CancellationToken)
            .ConfigureAwait(false);
        if (!resolved.IsSuccess) return ProviderOutcome<ProviderTrackMetadata>.Failure(resolved.Error!);
        if (request.ExpectedSnapshotVersion != null &&
            !string.Equals(request.ExpectedSnapshotVersion, resolved.RequireValue().ETag, StringComparison.Ordinal))
            return Failure<ProviderTrackMetadata>(ProviderErrorKind.PermanentFailure);

        var value = resolved.RequireValue();
        var metadata = TryMapTrack(value.Artist, new ArtistGridTracker(
            value.Tracker,
            value.Gid,
            value.Artist.Name,
            [],
            value.ETag,
            0), value.Track, value.Source);
        return metadata == null
            ? Failure<ProviderTrackMetadata>(ProviderErrorKind.NotFound)
            : ProviderOutcome<ProviderTrackMetadata>.Success(metadata);
    }

    public Task<ProviderOutcome<ProviderTrackMetadata>> LookupByIsrcAsync(
        ProviderExecutionContext context,
        ProviderIsrcLookupRequest request) =>
        Task.FromResult(Failure<ProviderTrackMetadata>(ProviderErrorKind.NotSupported));

    public Task<ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>> SearchAlbumsAsync(
        ProviderExecutionContext context,
        ProviderMetadataSearchRequest request) =>
        Task.FromResult(Failure<ProviderPage<ProviderAlbumMetadata>>(ProviderErrorKind.NotSupported));

    public Task<ProviderOutcome<ProviderAlbumMetadata>> GetAlbumAsync(
        ProviderExecutionContext context,
        ProviderAlbumLookupRequest request) =>
        Task.FromResult(Failure<ProviderAlbumMetadata>(ProviderErrorKind.NotSupported));

    public async Task<ProviderOutcome<ProviderPage<ProviderArtistMetadata>>> SearchArtistsAsync(
        ProviderExecutionContext context,
        ProviderMetadataSearchRequest request)
    {
        var error = ValidateContext(context);
        if (error != null) return Failure<ProviderPage<ProviderArtistMetadata>>(error);
        if (!TryOffset(request.Page.Cursor, out var offset))
            return Failure<ProviderPage<ProviderArtistMetadata>>(ProviderErrorKind.PermanentFailure);
        var index = await _catalog.GetArtistIndexAsync(context.CancellationToken).ConfigureAwait(false);
        if (!index.IsSuccess) return ProviderOutcome<ProviderPage<ProviderArtistMetadata>>.Failure(index.Error!);

        var tokens = Tokens(request.Query);
        var artists = index.RequireValue().Artists
            .Where(artist => tokens.Count == 0 || Tokens(artist.Name).All(tokens.Contains))
            .OrderBy(artist => Normalize(artist.Name), StringComparer.Ordinal)
            .ThenBy(artist => artist.Tracker, StringComparer.Ordinal)
            .Select(artist => new ProviderArtistMetadata(
                new ProviderExternalResourceId(ProviderId, ProviderResourceKind.Artist, artist.Tracker),
                artist.Name,
                artwork: null,
                snapshotVersion: null))
            .ToArray();
        var page = artists.Skip(offset).Take(request.Page.Limit).ToArray();
        var next = offset + page.Length < artists.Length
            ? (offset + page.Length).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
        return ProviderOutcome<ProviderPage<ProviderArtistMetadata>>.Success(new(
            ProviderId,
            page,
            next,
            next != null,
            null));
    }

    public async Task<ProviderOutcome<ProviderArtistMetadata>> GetArtistAsync(
        ProviderExecutionContext context,
        ProviderArtistLookupRequest request)
    {
        var error = ValidateContext(context, request.Id, ProviderResourceKind.Artist);
        if (error != null) return Failure<ProviderArtistMetadata>(error);
        if (!ArtistGridTrackId.TryNormalizeTracker(request.Id.Value, out var tracker))
            return Failure<ProviderArtistMetadata>(ProviderErrorKind.NotFound);
        var index = await _catalog.GetArtistIndexAsync(context.CancellationToken).ConfigureAwait(false);
        if (!index.IsSuccess) return ProviderOutcome<ProviderArtistMetadata>.Failure(index.Error!);
        var artist = index.RequireValue().Artists.FirstOrDefault(item =>
            item.Tracker.Equals(tracker, StringComparison.Ordinal));
        return artist == null
            ? Failure<ProviderArtistMetadata>(ProviderErrorKind.NotFound)
            : ProviderOutcome<ProviderArtistMetadata>.Success(new(
                request.Id,
                artist.Name,
                artwork: null,
                snapshotVersion: null));
    }

    public Task<ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>> GetArtistAlbumsAsync(
        ProviderExecutionContext context,
        ProviderArtistItemsRequest request) =>
        Task.FromResult(Failure<ProviderPage<ProviderAlbumMetadata>>(ProviderErrorKind.NotSupported));

    public async Task<ProviderOutcome<ProviderPage<ProviderTrackMetadata>>> GetArtistTracksAsync(
        ProviderExecutionContext context,
        ProviderArtistItemsRequest request)
    {
        var error = ValidateContext(context, request.Id, ProviderResourceKind.Artist);
        if (error != null) return Failure<ProviderPage<ProviderTrackMetadata>>(error);
        var artist = await GetArtistAsync(context, new ProviderArtistLookupRequest(request.Id,
            request.ExpectedSnapshotVersion)).ConfigureAwait(false);
        if (!artist.IsSuccess) return ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Failure(artist.Error!);
        var page = await SearchTracksAsync(context, new ProviderMetadataSearchRequest(
            artist.RequireValue().Name, request.Page)).ConfigureAwait(false);
        return page;
    }

    public Task<ProviderOutcome<ProviderPage<ProviderTrackMetadata>>> GetSimilarTracksAsync(
        ProviderExecutionContext context,
        ProviderSimilarTracksRequest request) =>
        Task.FromResult(Failure<ProviderPage<ProviderTrackMetadata>>(ProviderErrorKind.NotSupported));

    public Task<ProviderOutcome<ProviderPage<ProviderArtistMetadata>>> GetSimilarArtistsAsync(
        ProviderExecutionContext context,
        ProviderSimilarArtistsRequest request) =>
        Task.FromResult(Failure<ProviderPage<ProviderArtistMetadata>>(ProviderErrorKind.NotSupported));

    internal async Task<ProviderOutcome<ArtistGridResolvedTrack>> ResolveTrackAsync(
        ProviderExecutionContext context,
        ProviderExternalResourceId id)
    {
        var error = ValidateContext(context, id, ProviderResourceKind.Track);
        if (error != null) return Failure<ArtistGridResolvedTrack>(error);
        if (!ArtistGridTrackId.TryParse(id.Value, out var tracker, out var gid, out var sourceHash))
            return Failure<ArtistGridResolvedTrack>(ProviderErrorKind.NotFound);
        return await _catalog.ResolveTrackAsync(tracker, gid, sourceHash, context.CancellationToken)
            .ConfigureAwait(false);
    }

    private static ProviderError? ValidateContext(
        ProviderExecutionContext context,
        ProviderExternalResourceId? resourceId = null,
        ProviderResourceKind resourceKind = ProviderResourceKind.Unknown)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (resourceId != null)
        {
            try { context.RequireResourceOwner(resourceId, resourceKind); }
            catch (ArgumentException) { return new ProviderError(ProviderErrorKind.Forbidden); }
        }
        if (!context.ProviderId.Equals(ArtistGridConstants.ProviderId, StringComparison.Ordinal) ||
            !context.Policy.AllowsProvider(ArtistGridConstants.ProviderId) || context.Account != null)
            return new ProviderError(ProviderErrorKind.Forbidden);
        if (context.CancellationToken.IsCancellationRequested)
            return new ProviderError(ProviderErrorKind.Canceled);
        if (context.IsExpired(DateTimeOffset.UtcNow))
            return new ProviderError(ProviderErrorKind.CapabilityUnavailable);
        return null;
    }

    private static IReadOnlyList<ArtistMatch> FindArtists(
        ArtistGridArtistIndex index,
        IReadOnlyList<string> queryTokens)
    {
        var query = queryTokens.ToHashSet(StringComparer.Ordinal);
        return index.Artists
            .Select(artist =>
            {
                var artistTokens = Tokens(artist.Name);
                var matches = artistTokens.Count > 0 && artistTokens.All(query.Contains);
                var remaining = queryTokens.ToList();
                if (matches)
                {
                    foreach (var token in artistTokens)
                    {
                        var position = remaining.IndexOf(token);
                        if (position >= 0) remaining.RemoveAt(position);
                    }
                }
                return new ArtistMatch(artist, matches, remaining);
            })
            .Where(item => item.Matches)
            .OrderByDescending(item => Tokens(item.Artist.Name).Count)
            .ThenBy(item => Normalize(item.Artist.Name), StringComparer.Ordinal)
            .ThenBy(item => item.Artist.Tracker, StringComparer.Ordinal)
            .ToArray();
    }

    private static ProviderTrackMetadata? TryMapTrack(
        ArtistGridArtist artist,
        ArtistGridTracker tracker,
        ArtistGridRawTrack track,
        ArtistGridSource source)
    {
        if (!IsUsableTrack(track) || !SafeText(track.Title, 500) ||
            track.AlbumTitle != null && !SafeText(track.AlbumTitle, 500) ||
            track.Credits.Any(value => !SafeText(value, 300)) ||
            !SafeText(artist.Name, 500) ||
            artist.Credit != null && !SafeText(artist.Credit, 500)) return null;
        var idValue = ArtistGridTrackId.Create(tracker.Tracker, tracker.Gid, source.Hash);
        if (idValue.Length > 200 - "ext-artistgrid-song-".Length) return null;
        var artistName = artist.Name;
        var credits = new[] { artistName }.Concat(track.Credits).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var artistCredits = credits
            .Select((name, offset) => new ProviderArtistCredit(
                name,
                offset == 0
                    ? new ProviderExternalResourceId(ArtistGridConstants.ProviderId, ProviderResourceKind.Artist, artist.Tracker)
                    : null))
            .ToArray();
        return new ProviderTrackMetadata(
            new ProviderExternalResourceId(ArtistGridConstants.ProviderId, ProviderResourceKind.Track, idValue),
            track.Title,
            artistCredits,
            albumTitle: track.AlbumTitle,
            duration: track.Duration,
            snapshotVersion: tracker.ETag,
            albumArtist: artistName);
    }

    private static ArtistGridSource? SelectSource(IReadOnlyList<ArtistGridSource> sources) =>
        sources.OrderBy(source => source.CanonicalUrl, StringComparer.Ordinal).FirstOrDefault();

    private static bool IsUsableTrack(ArtistGridRawTrack track) =>
        track.Sources.Count > 0 && (string.IsNullOrWhiteSpace(track.AvailableLength) ||
            track.AvailableLength.Equals("full", StringComparison.OrdinalIgnoreCase));

    private static bool IsTitleMatch(string title, IReadOnlyList<string> tokens) =>
        tokens.Count == 0 || Tokens(title).ToHashSet(StringComparer.Ordinal).IsSupersetOf(tokens);

    private static IReadOnlyList<string> Tokens(string value)
    {
        var normalized = Normalize(value);
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var character in normalized)
        {
            if (char.IsLetterOrDigit(character)) current.Append(character);
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    private static string Normalize(string value) => ArtistGridCatalog.NormalizeForSearch(value);

    private static bool SafeText(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        value.Equals(value.Trim(), StringComparison.Ordinal) && !value.Any(char.IsControl);

    private static bool TryOffset(string? cursor, out int offset)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            offset = 0;
            return true;
        }
        return int.TryParse(cursor, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out offset) && offset >= 0;
    }

    private static ProviderOutcome<T> Failure<T>(ProviderErrorKind kind) =>
        ProviderOutcome<T>.Failure(new ProviderError(kind));

    private static ProviderOutcome<T> Failure<T>(ProviderError error) =>
        ProviderOutcome<T>.Failure(error);

    private sealed record ArtistMatch(
        ArtistGridArtist Artist,
        bool Matches,
        IReadOnlyList<string> RemainingTitleTokens);
}
