using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Intelligence;

public sealed class LocalRecommendationCatalog(IDbContextFactory<AllstarrDbContext> factory) : ILocalRecommendationCatalog
{
    public async Task<bool> HasCoverageAsync(IntelligenceScope scope, bool requireMusicBrainz, CancellationToken token)
    { await using var db = await factory.CreateDbContextAsync(token); var query = Scoped(db, scope); if (requireMusicBrainz) query = query.Where(x => x.MusicBrainzRecordingId != null || x.MusicBrainzArtistId != null || x.MusicBrainzReleaseId != null); return await query.AnyAsync(token); }
    public async Task<IReadOnlyList<RecommendationSourceItem>> FindRelatedAsync(ScopedRecommendationQuery query, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var tracks = await Scoped(db, query.Scope).AsNoTracking().ToListAsync(cancellationToken);
        var seedKeys = query.SeedTrackKeys.Select(NormalizeTrackKey).ToHashSet(StringComparer.Ordinal);
        var seeds = tracks.Where(track => seedKeys.Contains(track.BackendItemId) ||
            track.CanonicalRecordingId is { } canonical && seedKeys.Contains(canonical.ToString("D"))).ToArray();
        if (seeds.Length == 0) return [];
        var artists = seeds.SelectMany(track => new[] { track.Artist, track.AlbumArtist }).Where(value => !string.IsNullOrWhiteSpace(value)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var albums = seeds.Select(track => track.Album).Where(value => !string.IsNullOrWhiteSpace(value)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return tracks.Where(track => !seeds.Contains(track)).Select(track =>
        {
            var signals = new List<RecommendationSignal>();
            if (artists.Contains(track.Artist) || track.AlbumArtist != null && artists.Contains(track.AlbumArtist)) signals.Add(new("local-shared-artist", .75, "Shares an artist with a seed track."));
            if (track.Album != null && albums.Contains(track.Album)) signals.Add(new("local-shared-album", .65, "Appears on the same album as a seed track."));
            return new RecommendationSourceItem(track.BackendItemId, Math.Min(1, signals.Sum(signal => signal.Weight) / Math.Max(1, signals.Count)), signals,
                new("local", null, track.MusicBrainzRecordingId, track.Isrc, track.Title, track.Artist, track.Album, track.Id, track.BackendItemId));
        }).Where(item => item.Signals.Count > 0).OrderByDescending(item => item.Score).ThenBy(item => item.TrackKey, StringComparer.Ordinal).Take(query.Limit).ToArray();
    }

    public async Task<IReadOnlyDictionary<string, RecommendationTrackIdentity>> ResolveBackendItemsAsync(
        IntelligenceScope scope, IReadOnlyList<string> backendItemIds, CancellationToken cancellationToken)
    {
        IntelligencePolicyService.ValidateScope(scope);
        var ids = backendItemIds.Select(NormalizeTrackKey).Distinct(StringComparer.Ordinal).Take(201).ToArray();
        if (ids.Length > 200) throw new ArgumentOutOfRangeException(nameof(backendItemIds));
        if (ids.Length == 0) return new Dictionary<string, RecommendationTrackIdentity>(StringComparer.Ordinal);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await Scoped(db, scope).AsNoTracking().Where(item => ids.Contains(item.BackendItemId))
            .ToDictionaryAsync(item => item.BackendItemId, item => new RecommendationTrackIdentity(
                "local", null, item.MusicBrainzRecordingId, item.Isrc, item.Title, item.Artist,
                item.Album, item.Id, item.BackendItemId), StringComparer.Ordinal, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ResolveTrackKeysAsync(IntelligenceScope scope,
        IReadOnlyList<string> trackKeys, CancellationToken cancellationToken)
    {
        IntelligencePolicyService.ValidateScope(scope);
        ArgumentNullException.ThrowIfNull(trackKeys);
        if (trackKeys.Count > 100) throw new ArgumentOutOfRangeException(nameof(trackKeys));
        var keys = trackKeys.Select(item => item?.Trim() ?? "").ToArray();
        if (keys.Any(item => item.Length is < 1 or > 500 || item.Any(char.IsControl)))
            throw new ArgumentException("A track key is invalid.", nameof(trackKeys));
        var libraryIds = keys.Where(item => item.StartsWith("library:", StringComparison.Ordinal))
            .Select(item => Guid.TryParse(item[8..], out var id) ? id : Guid.Empty).ToArray();
        if (libraryIds.Contains(Guid.Empty)) throw new ArgumentException("A library track key is invalid.", nameof(trackKeys));
        var backendIds = keys.Where(item => !item.StartsWith("library:", StringComparison.Ordinal))
            .Select(NormalizeTrackKey).ToArray();
        var canonicalIds = backendIds.Select(item => Guid.TryParse(item, out var id) ? id : Guid.Empty)
            .Where(item => item != Guid.Empty).ToArray();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var tracks = await Scoped(db, scope).AsNoTracking().Where(item =>
                backendIds.Contains(item.BackendItemId) || libraryIds.Contains(item.Id) ||
                item.CanonicalRecordingId.HasValue && canonicalIds.Contains(item.CanonicalRecordingId.Value))
            .ToListAsync(cancellationToken);
        var resolved = new List<string>(keys.Length);
        foreach (var key in keys)
        {
            LibraryTrackRecord? track;
            if (key.StartsWith("library:", StringComparison.Ordinal))
                track = tracks.SingleOrDefault(item => item.Id == Guid.Parse(key[8..]));
            else
            {
                var normalized = NormalizeTrackKey(key);
                track = tracks.SingleOrDefault(item => item.BackendItemId == normalized);
                if (track == null && Guid.TryParse(normalized, out var canonical))
                {
                    var matches = tracks.Where(item => item.CanonicalRecordingId == canonical).Take(2).ToArray();
                    track = matches.Length == 1 ? matches[0] : null;
                }
            }
            if (track != null && !resolved.Contains(track.BackendItemId, StringComparer.Ordinal))
                resolved.Add(track.BackendItemId);
        }
        return resolved;
    }

    internal static IQueryable<LibraryTrackRecord> Scoped(AllstarrDbContext db, IntelligenceScope scope) => db.LibraryTracks.Where(track =>
        track.TenantId == scope.TenantId && track.OwnerUserId == scope.OwnerUserId && track.Protocol == scope.Protocol &&
        track.BackendInstanceId == scope.BackendInstanceId && track.LibraryScopeId == scope.LibraryScopeId);
    internal static string NormalizeTrackKey(string value) => value.StartsWith("backend:", StringComparison.Ordinal) ? value[8..] : value;
}

public sealed class MusicBrainzLocalRecommendationProvider(IDbContextFactory<AllstarrDbContext> factory)
    : IRecommendationProvider, IRecommendationProviderReadiness
{
    public string Id => "musicbrainz-local";
    public async Task<RecommendationProviderReadiness> GetReadinessAsync(IntelligenceScope scope, CancellationToken token = default)
    { await using var db = await factory.CreateDbContextAsync(token); return await LocalRecommendationCatalog.Scoped(db, scope).AnyAsync(x => x.MusicBrainzRecordingId != null || x.MusicBrainzArtistId != null || x.MusicBrainzReleaseId != null, token) ? new(Id, RecommendationProviderReadinessState.Ready) : new(Id, RecommendationProviderReadinessState.Degraded, "musicbrainz_local_coverage_missing"); }
    public async Task<RecommendationProviderResult> RecommendAsync(RecommendationRequest request)
    {
        if (!request.ExplicitlyOptedIn) return new(RecommendationProviderState.Disabled, [], "recommendation_opt_in_required");
        IntelligencePolicyService.ValidateScope(request.Scope);
        if (request.Profile.TenantId != request.Scope.TenantId || request.Profile.OwnerUserId != request.Scope.OwnerUserId ||
            request.Profile.BackendInstanceId != request.Scope.BackendInstanceId || request.Profile.LibraryScopeId != request.Scope.LibraryScopeId)
            return new(RecommendationProviderState.Unauthorized, [], "recommendation_scope_mismatch");
        try
        {
            await using var db = await factory.CreateDbContextAsync(request.CancellationToken);
            var tracks = await LocalRecommendationCatalog.Scoped(db, request.Scope).AsNoTracking().ToListAsync(request.CancellationToken);
            var keys = request.SeedTrackKeys.Concat(request.Profile.TopTrackKeys).Select(LocalRecommendationCatalog.NormalizeTrackKey).ToHashSet(StringComparer.Ordinal);
            var seeds = tracks.Where(track => keys.Contains(track.BackendItemId) || track.CanonicalRecordingId is { } id && keys.Contains(id.ToString("D"))).ToArray();
            var candidates = new List<RecommendationCandidate>();
            foreach (var track in tracks.Where(track => !seeds.Contains(track)))
            {
                var signals = new List<RecommendationSignal>();
                if (track.MusicBrainzArtistId != null && seeds.Any(seed => seed.MusicBrainzArtistId == track.MusicBrainzArtistId))
                    signals.Add(new("musicbrainz-shared-artist", .9, "Shares a MusicBrainz artist relationship with a seed recording."));
                if (track.MusicBrainzReleaseId != null && seeds.Any(seed => seed.MusicBrainzReleaseId == track.MusicBrainzReleaseId))
                    signals.Add(new("musicbrainz-shared-release", .85, "Belongs to the same MusicBrainz release as a seed recording."));
                if (track.MusicBrainzRecordingId != null && signals.Count > 0)
                    signals.Add(new("musicbrainz-recording-identified", .35, "The candidate has a verified MusicBrainz recording identity."));
                if (signals.Count > 0) candidates.Add(new(track.BackendItemId,
                    Math.Min(1, signals.Sum(signal => signal.Weight) / signals.Count), Id, signals,
                    new("musicbrainz", null, track.MusicBrainzRecordingId, track.Isrc, track.Title, track.Artist,
                        track.Album, track.Id, track.BackendItemId)));
            }
            return new(RecommendationProviderState.Succeeded, candidates.OrderByDescending(item => item.Score)
                .ThenBy(item => item.TrackKey, StringComparer.Ordinal).Take(request.Limit).ToArray());
        }
        catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested) { throw; }
        catch { return new(RecommendationProviderState.Degraded, [], "musicbrainz_local_temporarily_unavailable"); }
    }
}
