using System.Text.Json;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed record LibraryTrackIndexInput(
    string LibraryScopeId,
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
        string libraryScopeId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LocalTrackMatchCandidate>> GetMatchCandidatesAsync(
        ProtocolExecutionContext executionContext,
        string libraryScopeId,
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

    public LibraryIndexService(
        IDbContextFactory<AllstarrDbContext> contextFactory,
        DurableStorageState storageState,
        IPlatformClock clock)
    {
        _contextFactory = contextFactory;
        _storageState = storageState;
        _clock = clock;
    }

    public async Task<IndexedLibraryTrack> UpsertAsync(
        ProtocolExecutionContext executionContext,
        LibraryTrackIndexInput input,
        CancellationToken cancellationToken = default)
    {
        var principal = RequireScope(executionContext, input.LibraryScopeId);
        ValidateInput(input);
        EnsureStorageReady();
        cancellationToken.ThrowIfCancellationRequested();
        var providerIds = NormalizeProviderIds(input.ProviderTrackIds);

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var backendIdentity = await RequireBackendIdentityAsync(db, principal, cancellationToken);
        if (input.CanonicalRecordingId.HasValue &&
            !await db.CanonicalRecordings.AsNoTracking().AnyAsync(recording =>
                recording.Id == input.CanonicalRecordingId &&
                recording.TenantId == principal.TenantId,
                cancellationToken))
        {
            throw new KeyNotFoundException("The canonical recording is outside the indexed track tenant.");
        }

        var record = await db.LibraryTracks.SingleOrDefaultAsync(track =>
            track.TenantId == principal.TenantId &&
            track.OwnerUserId == principal.UserId &&
            track.LibraryScopeId == input.LibraryScopeId &&
            track.BackendInstanceId == principal.BackendInstanceId &&
            track.BackendItemId == input.BackendItemId,
            cancellationToken);
        var now = _clock.UtcNow;
        var created = record == null;
        record ??= new LibraryTrackRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = principal.TenantId,
            OwnerUserId = principal.UserId,
            BackendIdentityId = backendIdentity.Id,
            LibraryScopeId = input.LibraryScopeId,
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
        record.CanonicalRecordingId = input.CanonicalRecordingId;
        record.AcceptedDecisionVersion = input.AcceptedDecisionVersion;
        record.CoverArtReference = ValidateReference(input.CoverArtReference);
        record.SourceModifiedAt = input.SourceModifiedAt;
        record.UpdatedAt = now;
        if (created)
        {
            db.LibraryTracks.Add(record);
        }

        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = principal.TenantId,
            ActorUserId = principal.UserId,
            Category = "library-index",
            Action = created ? "track.create" : "track.update",
            Outcome = "succeeded",
            CorrelationId = executionContext.CorrelationId,
            DetailsJson = JsonSerializer.Serialize(new
            {
                libraryTrackId = record.Id,
                libraryScopeId = input.LibraryScopeId,
                backendInstanceId = principal.BackendInstanceId,
                hasCanonicalRecording = input.CanonicalRecordingId.HasValue
            }),
            CreatedAt = now
        });
        await db.SaveChangesAsync(cancellationToken);
        return Map(record);
    }

    public async Task<IReadOnlyList<IndexedLibraryTrack>> ListAsync(
        ProtocolExecutionContext executionContext,
        string libraryScopeId,
        CancellationToken cancellationToken = default)
    {
        var principal = RequireScope(executionContext, libraryScopeId);
        EnsureStorageReady();
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return (await ScopedQuery(db, principal, libraryScopeId)
                .OrderBy(track => track.Artist)
                .ThenBy(track => track.Album)
                .ThenBy(track => track.Title)
                .ToListAsync(cancellationToken))
            .Select(Map)
            .ToList();
    }

    public async Task<IReadOnlyList<LocalTrackMatchCandidate>> GetMatchCandidatesAsync(
        ProtocolExecutionContext executionContext,
        string libraryScopeId,
        CancellationToken cancellationToken = default)
    {
        var principal = RequireScope(executionContext, libraryScopeId);
        EnsureStorageReady();
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var tracks = await ScopedQuery(db, principal, libraryScopeId)
            .OrderBy(track => track.Id)
            .ToListAsync(cancellationToken);
        return tracks.Select(track => new LocalTrackMatchCandidate(
            track.Id,
            track.TenantId,
            track.OwnerUserId,
            track.BackendInstanceId,
            track.LibraryScopeId,
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

    private static IQueryable<LibraryTrackRecord> ScopedQuery(
        AllstarrDbContext db,
        Core.Identity.AllstarrPrincipal principal,
        string libraryScopeId) => db.LibraryTracks.AsNoTracking().Where(track =>
        track.TenantId == principal.TenantId &&
        track.OwnerUserId == principal.UserId &&
        track.LibraryScopeId == libraryScopeId &&
        track.BackendInstanceId == principal.BackendInstanceId);

    private static Core.Identity.AllstarrPrincipal RequireScope(
        ProtocolExecutionContext executionContext,
        string libraryScopeId)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        if (executionContext.Principal == null || executionContext.Actor?.UserId == null)
        {
            throw new UnauthorizedAccessException("A linked user is required to access the library index.");
        }

        if (string.IsNullOrWhiteSpace(libraryScopeId) ||
            executionContext.LibraryScopeId != null &&
            !executionContext.LibraryScopeId.Equals(libraryScopeId, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("The requested library is outside the protocol context.");
        }

        return executionContext.Principal;
    }

    private static async Task<BackendIdentityRecord> RequireBackendIdentityAsync(
        AllstarrDbContext db,
        Core.Identity.AllstarrPrincipal principal,
        CancellationToken cancellationToken) =>
        await db.BackendIdentities.SingleOrDefaultAsync(identity =>
            identity.TenantId == principal.TenantId &&
            identity.UserId == principal.UserId &&
            identity.BackendType == principal.BackendType &&
            identity.BackendInstanceId == principal.BackendInstanceId &&
            identity.PrincipalId == principal.BackendPrincipalId,
            cancellationToken)
        ?? throw new UnauthorizedAccessException("The linked backend identity no longer exists.");

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
