using System.Text.Json;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed record LibraryTrackIndexInput(
    string BackendLibraryId,
    string BackendItemId,
    string FilePath,
    string Title,
    string Artist,
    string? Album,
    string? AlbumArtist,
    long? DurationMilliseconds,
    string? DurationProvenance,
    DateTimeOffset? DurationRetrievedAt,
    string? Isrc,
    string? MusicBrainzRecordingId,
    string? MusicBrainzReleaseId,
    string? MusicBrainzArtistId,
    IReadOnlyDictionary<string, string>? ProviderTrackIds,
    Guid? CanonicalRecordingId,
    int? AcceptedDecisionVersion,
    string? CoverArtReference,
    DateTimeOffset SourceModifiedAt);

public sealed record IndexedLibraryTrack(
    Guid Id,
    string BackendItemId,
    string FilePath,
    string Title,
    string Artist,
    string? Album,
    string? AlbumArtist,
    long? DurationMilliseconds,
    string? DurationProvenance,
    DateTimeOffset? DurationRetrievedAt,
    string? Isrc,
    string? MusicBrainzRecordingId,
    Guid? CanonicalRecordingId,
    IReadOnlyDictionary<string, string> ProviderTrackIds,
    DateTimeOffset IndexedAt,
    DateTimeOffset SourceModifiedAt,
    long Revision);

public interface ILibraryIndexService
{
    Task<IndexedLibraryTrack> UpsertAsync(
        ProtocolExecutionContext executionContext,
        LibraryTrackIndexInput input,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IndexedLibraryTrack>> ListAsync(
        ProtocolExecutionContext executionContext,
        string backendLibraryId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LocalTrackMatchCandidate>> GetMatchCandidatesAsync(
        ProtocolExecutionContext executionContext,
        string backendLibraryId,
        CancellationToken cancellationToken = default);
}

public sealed class LibraryIndexService : ILibraryIndexService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IDbContextFactory<AllstarrDbContext> _contextFactory;
    private readonly DurableStorageState _storageState;
    private readonly IPlatformClock _clock;
    private readonly ITrackIdentityService _identities;
    private readonly IBackendLibraryAccessResolver _libraryAccess;

    public LibraryIndexService(
        IDbContextFactory<AllstarrDbContext> contextFactory,
        DurableStorageState storageState,
        IPlatformClock clock,
        ITrackIdentityService identities,
        IBackendLibraryAccessResolver libraryAccess)
    {
        _contextFactory = contextFactory;
        _storageState = storageState;
        _clock = clock;
        _identities = identities;
        _libraryAccess = libraryAccess;
    }

    public async Task<IndexedLibraryTrack> UpsertAsync(
        ProtocolExecutionContext executionContext,
        LibraryTrackIndexInput input,
        CancellationToken cancellationToken = default)
    {
        var principal = RequireScope(executionContext, input.BackendLibraryId);
        ValidateInput(input);
        EnsureStorageReady();
        cancellationToken.ThrowIfCancellationRequested();
        var providerIds = NormalizeProviderIds(input.ProviderTrackIds);

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        _ = await RequireUserAsync(db, principal, cancellationToken);
        var access = await _libraryAccess.ResolveAsync(executionContext, cancellationToken);
        if (!access.Allows(input.BackendLibraryId))
            throw new UnauthorizedAccessException("The backend library is not available to this user.");
        if (input.CanonicalRecordingId.HasValue &&
            !await db.CanonicalRecordings.AsNoTracking().AnyAsync(recording =>
                recording.Id == input.CanonicalRecordingId,
                cancellationToken))
        {
            throw new KeyNotFoundException("The canonical recording does not exist.");
        }

        var record = await db.LibraryTracks.SingleOrDefaultAsync(track =>
            track.OwnerUserId == principal.UserId &&
            track.BackendLibraryId == input.BackendLibraryId &&
            track.BackendInstanceId == principal.BackendInstanceId &&
            track.BackendItemId == input.BackendItemId,
            cancellationToken);
        var now = _clock.UtcNow;
        var created = record == null;
        record ??= new LibraryTrackRecord
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = principal.UserId,
            BackendLibraryId = input.BackendLibraryId,
            Protocol = principal.BackendType,
            BackendInstanceId = principal.BackendInstanceId,
            BackendItemId = input.BackendItemId,
            IndexedAt = now
        };
        record.FilePath = input.FilePath;
        record.Title = input.Title;
        record.Artist = input.Artist;
        record.Album = Clean(input.Album);
        record.AlbumArtist = Clean(input.AlbumArtist);
        record.DurationMilliseconds = input.DurationMilliseconds;
        record.DurationProvenance = input.DurationMilliseconds.HasValue ? Clean(input.DurationProvenance) : null;
        record.DurationRetrievedAt = input.DurationMilliseconds.HasValue ? input.DurationRetrievedAt : null;
        record.Isrc = Clean(input.Isrc)?.Replace("-", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        record.MusicBrainzRecordingId = NormalizeGuid(input.MusicBrainzRecordingId);
        record.MusicBrainzReleaseId = NormalizeGuid(input.MusicBrainzReleaseId);
        record.MusicBrainzArtistId = NormalizeGuid(input.MusicBrainzArtistId);
        record.ProviderIdsJson = JsonSerializer.Serialize(providerIds, JsonOptions);
        record.CanonicalRecordingId = input.CanonicalRecordingId ?? record.CanonicalRecordingId;
        record.AcceptedDecisionVersion = input.AcceptedDecisionVersion ?? record.AcceptedDecisionVersion;
        record.CoverArtReference = ValidateReference(input.CoverArtReference);
        record.SourceModifiedAt = input.SourceModifiedAt;
        record.IndexedAt = now;
        record.UpdatedAt = now;
        if (created)
        {
            db.LibraryTracks.Add(record);
        }

        var enrichment = "unchanged";
        if (!record.CanonicalRecordingId.HasValue)
        {
            var aliasNamespace = CanonicalCatalogKeys.NativeTrackNamespace(record.Protocol, record.BackendInstanceId);
            var itemHash = CanonicalCatalogKeys.Hash(record.BackendItemId);
            record.CanonicalRecordingId = await db.CanonicalCatalogAliases.AsNoTracking()
                .Where(alias => alias.Namespace == aliasNamespace &&
                    alias.EntityKind == CanonicalCatalogEntityKind.Recording &&
                    alias.ExternalIdHash == itemHash && alias.ExternalId == record.BackendItemId)
                .Select(alias => (Guid?)alias.CanonicalEntityId).SingleOrDefaultAsync(cancellationToken);
        }
        if (Guid.TryParse(record.MusicBrainzRecordingId, out var mbid) && mbid != Guid.Empty &&
            (!record.CanonicalRecordingId.HasValue || await db.CanonicalRecordings.AsNoTracking().AnyAsync(
                item => item.Id == record.CanonicalRecordingId &&
                    item.MusicBrainzRecordingId == mbid.ToString("D"), cancellationToken)))
        {
            try
            {
                var identity = await _identities.CreateRecordingAsync(
                    executionContext.RequireActor(), executionContext.CorrelationId,
                    record.Isrc, mbid.ToString("D"), cancellationToken);
                record.CanonicalRecordingId ??= identity.Recording.Id;
                enrichment = "linked";
            }
            catch (ArgumentException) { enrichment = "invalid-signals"; }
            catch (InvalidOperationException) { enrichment = "deferred"; }
        }

        await CanonicalCatalogIdentityProjection.ProjectLibraryTrackAsync(
            db,
            executionContext.RequireActor(),
            record,
            now,
            cancellationToken);

        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.CreateVersion7(),
            ActorUserId = principal.UserId,
            Category = "library-index",
            Action = created ? "track.create" : "track.update",
            Outcome = "succeeded",
            CorrelationId = executionContext.CorrelationId,
            DetailsJson = JsonSerializer.Serialize(new
            {
                libraryTrackId = record.Id,
                backendLibraryId = input.BackendLibraryId,
                backendInstanceId = principal.BackendInstanceId,
                hasCanonicalRecording = record.CanonicalRecordingId.HasValue,
                canonicalEnrichment = enrichment
            }),
            CreatedAt = now
        });
        await db.SaveChangesAsync(cancellationToken);
        return Map(record);
    }

    public async Task<IReadOnlyList<IndexedLibraryTrack>> ListAsync(
        ProtocolExecutionContext executionContext,
        string backendLibraryId,
        CancellationToken cancellationToken = default)
    {
        _ = RequireScope(executionContext, backendLibraryId);
        EnsureStorageReady();
        var access = await _libraryAccess.ResolveAsync(executionContext, cancellationToken);
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return (await LibraryTrackAccess.Query(db, executionContext, access).Where(track => track.BackendLibraryId == backendLibraryId)
                .OrderBy(track => track.Artist)
                .ThenBy(track => track.Album)
                .ThenBy(track => track.Title)
                .ToListAsync(cancellationToken))
            .Select(Map)
            .ToList();
    }

    public async Task<IReadOnlyList<LocalTrackMatchCandidate>> GetMatchCandidatesAsync(
        ProtocolExecutionContext executionContext,
        string backendLibraryId,
        CancellationToken cancellationToken = default)
    {
        _ = RequireScope(executionContext, backendLibraryId);
        EnsureStorageReady();
        var access = await _libraryAccess.ResolveAsync(executionContext, cancellationToken);
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var tracks = await LibraryTrackAccess.Query(db, executionContext, access).Where(track => track.BackendLibraryId == backendLibraryId)
            .OrderBy(track => track.Id)
            .ToListAsync(cancellationToken);
        return tracks.Select(track => new LocalTrackMatchCandidate(
            track.Id,
            track.OwnerUserId,
            track.BackendInstanceId,
            track.BackendLibraryId,
            track.BackendItemId,
            track.CanonicalRecordingId,
            track.Title,
            track.Artist,
            track.Album,
            track.AlbumArtist,
            track.DurationMilliseconds,
            track.Isrc,
            track.MusicBrainzRecordingId,
            IsExplicit: null,
            ParseProviderIds(track.ProviderIdsJson))).ToList();
    }

    private static Core.Identity.AllstarrPrincipal RequireScope(
        ProtocolExecutionContext executionContext,
        string backendLibraryId)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        if (executionContext.Principal == null || executionContext.Actor?.UserId == null)
        {
            throw new UnauthorizedAccessException("A linked user is required to access the library index.");
        }

        if (string.IsNullOrWhiteSpace(backendLibraryId))
        {
            throw new UnauthorizedAccessException("A backend library is required.");
        }

        return executionContext.Principal;
    }

    private static async Task<UserRecord> RequireUserAsync(
        AllstarrDbContext db,
        Core.Identity.AllstarrPrincipal principal,
        CancellationToken cancellationToken) =>
        await db.Users.SingleOrDefaultAsync(user =>
            user.Id == principal.UserId && user.Enabled &&
            user.BackendType == principal.BackendType &&
            user.BackendInstanceId == principal.BackendInstanceId &&
            user.BackendPrincipalId == principal.BackendPrincipalId,
            cancellationToken)
        ?? throw new UnauthorizedAccessException("The linked user is no longer available.");

    private static void ValidateInput(LibraryTrackIndexInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.BackendItemId) ||
            string.IsNullOrWhiteSpace(input.FilePath) ||
            string.IsNullOrWhiteSpace(input.Title) ||
            string.IsNullOrWhiteSpace(input.Artist) ||
            input.DurationMilliseconds is <= 0 ||
            input.DurationMilliseconds.HasValue &&
                (string.IsNullOrWhiteSpace(input.DurationProvenance) ||
                 input.DurationRetrievedAt is null ||
                 input.DurationRetrievedAt == default) ||
            !input.DurationMilliseconds.HasValue &&
                (input.DurationProvenance != null || input.DurationRetrievedAt.HasValue) ||
            input.SourceModifiedAt == default ||
            input.AcceptedDecisionVersion is <= 0)
        {
            throw new ArgumentException("The library track input is incomplete or invalid.", nameof(input));
        }
    }

    private static IReadOnlyDictionary<string, string> NormalizeProviderIds(
        IReadOnlyDictionary<string, string>? values)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (provider, value) in values ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(provider) ||
                string.IsNullOrWhiteSpace(value) ||
                provider.Length > 100 ||
                value.Length > 500 ||
                value.Contains("://", StringComparison.Ordinal) ||
                value.Contains("token=", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Provider IDs must be opaque, bounded, and secret-free.", nameof(values));
            }

            result.Add(provider.Trim().ToLowerInvariant(), value.Trim());
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> ParseProviderIds(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions)
        ?? new Dictionary<string, string>();

    private static IndexedLibraryTrack Map(LibraryTrackRecord record) => new(
        record.Id,
        record.BackendItemId,
        record.FilePath,
        record.Title,
        record.Artist,
        record.Album,
        record.AlbumArtist,
        record.DurationMilliseconds,
        record.DurationProvenance,
        record.DurationRetrievedAt,
        record.Isrc,
        record.MusicBrainzRecordingId,
        record.CanonicalRecordingId,
        ParseProviderIds(record.ProviderIdsJson),
        record.IndexedAt,
        record.SourceModifiedAt,
        record.Revision);

    private static string? NormalizeGuid(string? value) =>
        Guid.TryParse(value, out var parsed) ? parsed.ToString("D") : Clean(value);

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ValidateReference(string? value)
    {
        value = Clean(value);
        if (value != null &&
            (value.Contains("?", StringComparison.Ordinal) ||
             value.Contains("://", StringComparison.Ordinal) ||
             value.Contains("token", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Cover art must use a stable backend or provider reference.", nameof(value));
        }

        return value;
    }

    private void EnsureStorageReady()
    {
        if (_storageState.GetSnapshot().Readiness != DurableStorageReadiness.Ready)
        {
            throw new InvalidOperationException("Durable storage is not ready.");
        }
    }
}
