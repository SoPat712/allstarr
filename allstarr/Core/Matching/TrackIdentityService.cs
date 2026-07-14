using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using allstarr.Core.Capabilities;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public enum TrackIdentityLinkStatus
{
    Created,
    AlreadyLinked,
    Conflict
}

public enum TrackIdentityTranslationStatus
{
    Translated,
    SourceNotLinked,
    TargetNotLinked,
    TargetAmbiguous
}

public sealed record CanonicalRecordingIdentity(
    Guid Id,
    Guid TenantId,
    Guid CreatedByUserId,
    string? Isrc,
    string? MusicBrainzRecordingId,
    long Revision);

public sealed record CanonicalRecordingCreationResult(
    CanonicalRecordingIdentity Recording,
    bool Created);

public sealed record TrackIdentityLinkRequest(
    Guid CanonicalRecordingId,
    ProviderExternalResourceId ExternalId,
    ProviderIdentityScope Scope,
    ProviderIdentityVerification Verification,
    string VerificationMethod,
    int DecisionVersion);

public sealed record TrackIdentityLinkResult(
    TrackIdentityLinkStatus Status,
    Guid CanonicalRecordingId,
    Guid? LinkId,
    Guid? ConflictingCanonicalRecordingId);

public sealed record TrackIdentityResolution(
    Guid CanonicalRecordingId,
    Guid LinkId,
    ProviderExternalResourceId ExternalId,
    ProviderIdentityScope Scope,
    Guid? ProviderAccountId,
    ProviderIdentityVerification Verification,
    string VerificationMethod,
    int DecisionVersion);

public sealed record ProviderTrackIdentityTarget
{
    public ProviderTrackIdentityTarget(
        string providerId,
        ProviderResourceKind resourceKind = ProviderResourceKind.Track,
        string? catalog = null)
    {
        ProviderId = ProviderContractValidation.ProviderId(providerId, nameof(providerId));
        if (!Enum.IsDefined(resourceKind) || resourceKind == ProviderResourceKind.Unknown)
        {
            throw new ArgumentOutOfRangeException(nameof(resourceKind));
        }

        ResourceKind = resourceKind;
        Catalog = catalog == null
            ? null
            : ProviderContractValidation.Catalog(catalog, nameof(catalog));
    }

    public string ProviderId { get; }

    public ProviderResourceKind ResourceKind { get; }

    public string? Catalog { get; }
}

public sealed record TrackIdentityTranslationResult(
    TrackIdentityTranslationStatus Status,
    Guid? CanonicalRecordingId,
    TrackIdentityResolution? Source,
    TrackIdentityResolution? Target);

public interface ITrackIdentityService
{
    Task<CanonicalRecordingCreationResult> CreateRecordingAsync(
        ProviderActorContext actor,
        string correlationId,
        string? isrc = null,
        string? musicBrainzRecordingId = null,
        CancellationToken cancellationToken = default);

    Task<TrackIdentityLinkResult> LinkAsync(
        ProviderExecutionContext executionContext,
        TrackIdentityLinkRequest request,
        CancellationToken cancellationToken = default);

    Task<TrackIdentityResolution?> ResolveAsync(
        ProviderExecutionContext executionContext,
        ProviderExternalResourceId externalId,
        CancellationToken cancellationToken = default);

    Task<TrackIdentityTranslationResult> TranslateAsync(
        ProviderExecutionContext sourceContext,
        ProviderExternalResourceId sourceId,
        ProviderExecutionContext targetContext,
        ProviderTrackIdentityTarget target,
        CancellationToken cancellationToken = default);
}

public sealed class TrackIdentityService : ITrackIdentityService
{
    private const string DefaultCatalog = "default";
    private static readonly Regex IsrcPattern = new(
        "^[A-Z]{2}[A-Z0-9]{3}[0-9]{7}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private readonly IDbContextFactory<AllstarrDbContext> _contextFactory;
    private readonly DurableStorageState _storageState;
    private readonly IPlatformClock _clock;

    public TrackIdentityService(
        IDbContextFactory<AllstarrDbContext> contextFactory,
        DurableStorageState storageState,
        IPlatformClock clock)
    {
        _contextFactory = contextFactory;
        _storageState = storageState;
        _clock = clock;
    }

    public async Task<CanonicalRecordingCreationResult> CreateRecordingAsync(
        ProviderActorContext actor,
        string correlationId,
        string? isrc = null,
        string? musicBrainzRecordingId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        EnsureStorageReady();
        cancellationToken.ThrowIfCancellationRequested();
        correlationId = ProviderContractValidation.RequiredText(
            correlationId,
            nameof(correlationId),
            100);
        var normalizedIsrc = NormalizeIsrc(isrc);
        var normalizedMusicBrainzId = NormalizeMusicBrainzRecordingId(musicBrainzRecordingId);
        var userId = actor.UserId ?? throw new UnauthorizedAccessException(
            "Creating a canonical recording requires a user actor.");

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await ValidateActorAsync(context, actor, cancellationToken);
        var existing = await FindCanonicalByExactSignalsAsync(
            context,
            actor.TenantId,
            normalizedIsrc,
            normalizedMusicBrainzId,
            cancellationToken);
        if (existing != null)
        {
            EnsureSignalsCompatible(existing, normalizedIsrc, normalizedMusicBrainzId);
            AddAudit(
                context,
                actor,
                correlationId,
                "canonical-recording.create",
                "already-exists",
                new
                {
                    canonicalRecordingId = existing.Id,
                    hasIsrc = normalizedIsrc != null,
                    hasMusicBrainzRecordingId = normalizedMusicBrainzId != null
                });
            await context.SaveChangesAsync(cancellationToken);
            return new CanonicalRecordingCreationResult(ToIdentity(existing), Created: false);
        }

        var now = _clock.UtcNow;
        var record = new CanonicalRecordingRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = actor.TenantId,
            CreatedByUserId = userId,
            Isrc = normalizedIsrc,
            MusicBrainzRecordingId = normalizedMusicBrainzId,
            CreatedAt = now,
            UpdatedAt = now
        };
        context.CanonicalRecordings.Add(record);
        AddAudit(
            context,
            actor,
            correlationId,
            "canonical-recording.create",
            "created",
            new
            {
                canonicalRecordingId = record.Id,
                hasIsrc = normalizedIsrc != null,
                hasMusicBrainzRecordingId = normalizedMusicBrainzId != null
            });

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return new CanonicalRecordingCreationResult(ToIdentity(record), Created: true);
        }
        catch (DbUpdateException)
        {
            context.ChangeTracker.Clear();
            existing = await FindCanonicalByExactSignalsAsync(
                context,
                actor.TenantId,
                normalizedIsrc,
                normalizedMusicBrainzId,
                cancellationToken);
            if (existing == null)
            {
                throw;
            }

            EnsureSignalsCompatible(existing, normalizedIsrc, normalizedMusicBrainzId);
            AddAudit(
                context,
                actor,
                correlationId,
                "canonical-recording.create",
                "concurrent-existing",
                new { canonicalRecordingId = existing.Id });
            await context.SaveChangesAsync(cancellationToken);
            return new CanonicalRecordingCreationResult(ToIdentity(existing), Created: false);
        }
    }

    public async Task<TrackIdentityLinkResult> LinkAsync(
        ProviderExecutionContext executionContext,
        TrackIdentityLinkRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(request);
        EnsureStorageReady();
        ValidateLinkRequest(executionContext, request);
        ThrowIfUnavailable(executionContext, cancellationToken);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var account = await ValidateExecutionContextAsync(
            context,
            executionContext,
            cancellationToken);
        var canonicalExists = await context.CanonicalRecordings.AsNoTracking().AnyAsync(
            item => item.Id == request.CanonicalRecordingId &&
                    item.TenantId == executionContext.Actor.TenantId,
            cancellationToken);
        if (!canonicalExists)
        {
            throw new KeyNotFoundException("The canonical recording does not exist in the actor tenant.");
        }

        var scopeAccountId = request.Scope == ProviderIdentityScope.Account
            ? account!.Id
            : (Guid?)null;
        var key = ExactKey(request.ExternalId);
        var existing = await FindExactLinkAsync(
            context,
            executionContext.Actor.TenantId,
            key,
            request.Scope,
            scopeAccountId,
            tracking: true,
            cancellationToken);
        if (existing != null)
        {
            return await CompleteExistingLinkAsync(
                context,
                executionContext,
                request,
                existing,
                cancellationToken);
        }

        var now = _clock.UtcNow;
        var link = new ProviderTrackIdentityRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = executionContext.Actor.TenantId,
            CanonicalRecordingId = request.CanonicalRecordingId,
            ProviderAccountId = scopeAccountId,
            ProviderId = key.ProviderId,
            ResourceKind = key.ResourceKind,
            CatalogNamespace = key.Catalog,
            Scope = request.Scope,
            ExternalId = request.ExternalId.Value,
            ExternalIdHash = key.ExternalIdHash,
            Verification = request.Verification,
            VerificationMethod = request.VerificationMethod,
            DecisionVersion = request.DecisionVersion,
            VerifiedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
        context.ProviderTrackIdentities.Add(link);
        AddLinkAudit(context, executionContext, request, "created", link.Id, null);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return new TrackIdentityLinkResult(
                TrackIdentityLinkStatus.Created,
                request.CanonicalRecordingId,
                link.Id,
                null);
        }
        catch (DbUpdateException)
        {
            context.ChangeTracker.Clear();
            existing = await FindExactLinkAsync(
                context,
                executionContext.Actor.TenantId,
                key,
                request.Scope,
                scopeAccountId,
                tracking: true,
                cancellationToken);
            if (existing == null)
            {
                throw;
            }

            return await CompleteExistingLinkAsync(
                context,
                executionContext,
                request,
                existing,
                cancellationToken,
                concurrent: true);
        }
    }

    public async Task<TrackIdentityResolution?> ResolveAsync(
        ProviderExecutionContext executionContext,
        ProviderExternalResourceId externalId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(externalId);
        EnsureStorageReady();
        RequireTrack(externalId);
        externalId.RequireOwner(executionContext.ProviderId, ProviderResourceKind.Track);
        ThrowIfUnavailable(executionContext, cancellationToken);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var account = await ValidateExecutionContextAsync(
            context,
            executionContext,
            cancellationToken);
        return await ResolveCoreAsync(
            context,
            executionContext.Actor.TenantId,
            externalId,
            account?.Id,
            cancellationToken);
    }

    public async Task<TrackIdentityTranslationResult> TranslateAsync(
        ProviderExecutionContext sourceContext,
        ProviderExternalResourceId sourceId,
        ProviderExecutionContext targetContext,
        ProviderTrackIdentityTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceContext);
        ArgumentNullException.ThrowIfNull(sourceId);
        ArgumentNullException.ThrowIfNull(targetContext);
        ArgumentNullException.ThrowIfNull(target);
        EnsureStorageReady();
        RequireSameActor(sourceContext.Actor, targetContext.Actor);
        RequireTrack(sourceId);
        if (target.ResourceKind != ProviderResourceKind.Track)
        {
            throw new ArgumentException("Canonical recording translation only accepts track targets.", nameof(target));
        }

        sourceId.RequireOwner(sourceContext.ProviderId, ProviderResourceKind.Track);
        if (!target.ProviderId.Equals(targetContext.ProviderId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The target belongs to another provider.", nameof(target));
        }

        ThrowIfUnavailable(sourceContext, cancellationToken);
        ThrowIfUnavailable(targetContext, cancellationToken);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var sourceAccount = await ValidateExecutionContextAsync(
            context,
            sourceContext,
            cancellationToken);
        var targetAccount = await ValidateExecutionContextAsync(
            context,
            targetContext,
            cancellationToken);
        var source = await ResolveCoreAsync(
            context,
            sourceContext.Actor.TenantId,
            sourceId,
            sourceAccount?.Id,
            cancellationToken);
        if (source == null)
        {
            return new TrackIdentityTranslationResult(
                TrackIdentityTranslationStatus.SourceNotLinked,
                null,
                null,
                null);
        }

        var catalog = target.Catalog ?? DefaultCatalog;
        var candidates = await context.ProviderTrackIdentities.AsNoTracking()
            .Where(item =>
                item.TenantId == sourceContext.Actor.TenantId &&
                item.CanonicalRecordingId == source.CanonicalRecordingId &&
                item.ProviderId == target.ProviderId &&
                item.ResourceKind == target.ResourceKind &&
                item.CatalogNamespace == catalog &&
                (item.Scope == ProviderIdentityScope.Catalog ||
                 (targetAccount != null &&
                  item.Scope == ProviderIdentityScope.Account &&
                  item.ProviderAccountId == targetAccount.Id)))
            .ToListAsync(cancellationToken);
        var preferred = PreferAccountScope(candidates, targetAccount?.Id);
        if (preferred.Count == 0)
        {
            return new TrackIdentityTranslationResult(
                TrackIdentityTranslationStatus.TargetNotLinked,
                source.CanonicalRecordingId,
                source,
                null);
        }

        if (preferred.Count > 1)
        {
            return new TrackIdentityTranslationResult(
                TrackIdentityTranslationStatus.TargetAmbiguous,
                source.CanonicalRecordingId,
                source,
                null);
        }

        return new TrackIdentityTranslationResult(
            TrackIdentityTranslationStatus.Translated,
            source.CanonicalRecordingId,
            source,
            ToResolution(preferred[0]));
    }

    private async Task<TrackIdentityLinkResult> CompleteExistingLinkAsync(
        AllstarrDbContext context,
        ProviderExecutionContext executionContext,
        TrackIdentityLinkRequest request,
        ProviderTrackIdentityRecord existing,
        CancellationToken cancellationToken,
        bool concurrent = false)
    {
        EnsureExactExternalId(existing, request.ExternalId.Value);
        var sameCanonical = existing.CanonicalRecordingId == request.CanonicalRecordingId;
        var outcome = sameCanonical
            ? concurrent ? "concurrent-existing" : "already-linked"
            : "conflict";
        AddLinkAudit(
            context,
            executionContext,
            request,
            outcome,
            existing.Id,
            sameCanonical ? null : existing.CanonicalRecordingId);
        await context.SaveChangesAsync(cancellationToken);
        return new TrackIdentityLinkResult(
            sameCanonical
                ? TrackIdentityLinkStatus.AlreadyLinked
                : TrackIdentityLinkStatus.Conflict,
            request.CanonicalRecordingId,
            existing.Id,
            sameCanonical ? null : existing.CanonicalRecordingId);
    }

    private async Task<TrackIdentityResolution?> ResolveCoreAsync(
        AllstarrDbContext context,
        Guid tenantId,
        ProviderExternalResourceId externalId,
        Guid? providerAccountId,
        CancellationToken cancellationToken)
    {
        var key = ExactKey(externalId);
        var candidates = await context.ProviderTrackIdentities.AsNoTracking()
            .Where(item =>
                item.TenantId == tenantId &&
                item.ProviderId == key.ProviderId &&
                item.ResourceKind == key.ResourceKind &&
                item.CatalogNamespace == key.Catalog &&
                item.ExternalIdHash == key.ExternalIdHash &&
                (item.Scope == ProviderIdentityScope.Catalog ||
                 (providerAccountId != null &&
                  item.Scope == ProviderIdentityScope.Account &&
                  item.ProviderAccountId == providerAccountId)))
            .ToListAsync(cancellationToken);
        foreach (var candidate in candidates)
        {
            EnsureExactExternalId(candidate, externalId.Value);
        }

        var preferred = PreferAccountScope(candidates, providerAccountId);
        return preferred.Count switch
        {
            0 => null,
            1 => ToResolution(preferred[0]),
            _ => throw new InvalidOperationException(
                "More than one accepted track identity exists in the same exact scope.")
        };
    }

    private static async Task<ProviderTrackIdentityRecord?> FindExactLinkAsync(
        AllstarrDbContext context,
        Guid tenantId,
        ExactTrackIdentityKey key,
        ProviderIdentityScope scope,
        Guid? providerAccountId,
        bool tracking,
        CancellationToken cancellationToken)
    {
        var query = context.ProviderTrackIdentities.Where(item =>
            item.TenantId == tenantId &&
            item.ProviderId == key.ProviderId &&
            item.ResourceKind == key.ResourceKind &&
            item.CatalogNamespace == key.Catalog &&
            item.Scope == scope &&
            item.ProviderAccountId == providerAccountId &&
            item.ExternalIdHash == key.ExternalIdHash);
        if (!tracking)
        {
            query = query.AsNoTracking();
        }

        return await query.SingleOrDefaultAsync(cancellationToken);
    }

    private async Task<ProviderAccountRecord?> ValidateExecutionContextAsync(
        AllstarrDbContext context,
        ProviderExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        await ValidateActorAsync(context, executionContext.Actor, cancellationToken);
        if (executionContext.Account == null)
        {
            return null;
        }

        var account = await context.ProviderAccounts.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == executionContext.Account.AccountId,
            cancellationToken);
        if (account == null || !account.Enabled)
        {
            throw new UnauthorizedAccessException("The provider account is unavailable.");
        }

        var snapshotMatches = account.ProviderId.Equals(executionContext.ProviderId, StringComparison.Ordinal) &&
                              account.Scope == executionContext.Account.Scope &&
                              account.TenantId == executionContext.Account.TenantId &&
                              account.OwnerUserId == executionContext.Account.OwnerUserId &&
                              string.Equals(
                                  account.LibraryScopeId,
                                  executionContext.Account.LibraryScopeId,
                                  StringComparison.Ordinal) &&
                              account.Revision == executionContext.Account.Revision;
        if (!snapshotMatches)
        {
            throw new UnauthorizedAccessException(
                "The provider account context is stale or outside the actor scope.");
        }

        if (account.Scope != ProviderAccountScope.Global &&
            account.TenantId != executionContext.Actor.TenantId)
        {
            throw new UnauthorizedAccessException("The provider account belongs to another tenant.");
        }

        return account;
    }

    private static async Task ValidateActorAsync(
        AllstarrDbContext context,
        ProviderActorContext actor,
        CancellationToken cancellationToken)
    {
        if (actor.UserId == null)
        {
            return;
        }

        var valid = await context.Users.AsNoTracking().AnyAsync(
            item => item.Id == actor.UserId &&
                    item.TenantId == actor.TenantId &&
                    item.Status == PlatformUserStatus.Active,
            cancellationToken);
        if (!valid)
        {
            throw new UnauthorizedAccessException("The provider actor is not active in the requested tenant.");
        }
    }

    private static async Task<CanonicalRecordingRecord?> FindCanonicalByExactSignalsAsync(
        AllstarrDbContext context,
        Guid tenantId,
        string? isrc,
        string? musicBrainzRecordingId,
        CancellationToken cancellationToken)
    {
        if (isrc == null && musicBrainzRecordingId == null)
        {
            return null;
        }

        var candidates = await context.CanonicalRecordings.AsNoTracking()
            .Where(item => item.TenantId == tenantId &&
                ((isrc != null && item.Isrc == isrc) ||
                 (musicBrainzRecordingId != null &&
                  item.MusicBrainzRecordingId == musicBrainzRecordingId)))
            .ToListAsync(cancellationToken);
        return candidates.Count switch
        {
            0 => null,
            1 => candidates[0],
            _ => throw new InvalidOperationException(
                "The supplied exact recording signals resolve to different canonical recordings.")
        };
    }

    private static void EnsureSignalsCompatible(
        CanonicalRecordingRecord existing,
        string? isrc,
        string? musicBrainzRecordingId)
    {
        if (isrc != null && existing.Isrc != null &&
            !existing.Isrc.Equals(isrc, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The ISRC conflicts with the existing canonical recording.");
        }

        if (musicBrainzRecordingId != null && existing.MusicBrainzRecordingId != null &&
            !existing.MusicBrainzRecordingId.Equals(musicBrainzRecordingId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The MusicBrainz recording ID conflicts with the existing canonical recording.");
        }
    }

    private static IReadOnlyList<ProviderTrackIdentityRecord> PreferAccountScope(
        IReadOnlyList<ProviderTrackIdentityRecord> candidates,
        Guid? providerAccountId)
    {
        if (providerAccountId != null)
        {
            var account = candidates.Where(item =>
                item.Scope == ProviderIdentityScope.Account &&
                item.ProviderAccountId == providerAccountId).ToArray();
            if (account.Length > 0)
            {
                return account;
            }
        }

        return candidates.Where(item => item.Scope == ProviderIdentityScope.Catalog).ToArray();
    }

    private static void ValidateLinkRequest(
        ProviderExecutionContext executionContext,
        TrackIdentityLinkRequest request)
    {
        if (request.CanonicalRecordingId == Guid.Empty)
        {
            throw new ArgumentException("A canonical recording ID is required.", nameof(request));
        }

        ArgumentNullException.ThrowIfNull(request.ExternalId);
        RequireTrack(request.ExternalId);
        request.ExternalId.RequireOwner(executionContext.ProviderId, ProviderResourceKind.Track);
        if (request.Scope is not ProviderIdentityScope.Catalog and not ProviderIdentityScope.Account)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The identity scope is invalid.");
        }

        if (request.Scope == ProviderIdentityScope.Account && executionContext.Account == null)
        {
            throw new ArgumentException(
                "An account-scoped identity requires an authorized provider account.",
                nameof(request));
        }

        if (request.Verification is not ProviderIdentityVerification.Verified and
            not ProviderIdentityVerification.Pinned)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The verification state is invalid.");
        }

        ProviderContractValidation.RequiredText(
            request.VerificationMethod,
            nameof(request.VerificationMethod),
            50);
        if (request.DecisionVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Decision versions start at one.");
        }

        if (request.Verification == ProviderIdentityVerification.Pinned &&
            executionContext.Actor.Kind == ProviderActorKind.SystemJob)
        {
            throw new UnauthorizedAccessException("Automated jobs cannot create pinned identity links.");
        }
    }

    private void ThrowIfUnavailable(
        ProviderExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        executionContext.CancellationToken.ThrowIfCancellationRequested();
        if (executionContext.IsExpired(_clock.UtcNow))
        {
            throw new TimeoutException("The provider execution deadline has expired.");
        }
    }

    private void EnsureStorageReady()
    {
        if (_storageState.GetSnapshot().Readiness != DurableStorageReadiness.Ready)
        {
            throw new InvalidOperationException("Durable storage is not ready.");
        }
    }

    private static void RequireTrack(ProviderExternalResourceId externalId)
    {
        if (externalId.ResourceKind != ProviderResourceKind.Track)
        {
            throw new ArgumentException("Track identity operations require a track resource ID.", nameof(externalId));
        }
    }

    private static void RequireSameActor(ProviderActorContext source, ProviderActorContext target)
    {
        if (source.TenantId != target.TenantId ||
            source.Kind != target.Kind ||
            source.UserId != target.UserId ||
            source.DurableJobId != target.DurableJobId)
        {
            throw new UnauthorizedAccessException(
                "Source and target provider contexts must belong to the same actor.");
        }
    }

    private static ExactTrackIdentityKey ExactKey(ProviderExternalResourceId externalId) => new(
        externalId.ProviderId,
        externalId.ResourceKind,
        externalId.Catalog ?? DefaultCatalog,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(externalId.Value)))
            .ToLowerInvariant());

    private static void EnsureExactExternalId(
        ProviderTrackIdentityRecord record,
        string externalId)
    {
        if (!record.ExternalId.Equals(externalId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "An external identity hash collision was detected; no match was accepted.");
        }
    }

    private static string? NormalizeIsrc(string? value)
    {
        if (value == null)
        {
            return null;
        }

        var candidate = value.Trim().Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();
        if (!IsrcPattern.IsMatch(candidate))
        {
            throw new ArgumentException("ISRC must contain a valid 12-character recording code.", nameof(value));
        }

        return candidate;
    }

    private static string? NormalizeMusicBrainzRecordingId(string? value)
    {
        if (value == null)
        {
            return null;
        }

        if (!Guid.TryParse(value.Trim(), out var id) || id == Guid.Empty)
        {
            throw new ArgumentException(
                "MusicBrainz recording ID must be a non-empty UUID.",
                nameof(value));
        }

        return id.ToString("D");
    }

    private static CanonicalRecordingIdentity ToIdentity(CanonicalRecordingRecord record) => new(
        record.Id,
        record.TenantId,
        record.CreatedByUserId,
        record.Isrc,
        record.MusicBrainzRecordingId,
        record.Revision);

    private static TrackIdentityResolution ToResolution(ProviderTrackIdentityRecord record) => new(
        record.CanonicalRecordingId,
        record.Id,
        new ProviderExternalResourceId(
            record.ProviderId,
            record.ResourceKind,
            record.ExternalId,
            record.CatalogNamespace == DefaultCatalog ? null : record.CatalogNamespace),
        record.Scope,
        record.ProviderAccountId,
        record.Verification,
        record.VerificationMethod,
        record.DecisionVersion);

    private static void AddLinkAudit(
        AllstarrDbContext context,
        ProviderExecutionContext executionContext,
        TrackIdentityLinkRequest request,
        string outcome,
        Guid linkId,
        Guid? conflictingCanonicalRecordingId)
    {
        AddAudit(
            context,
            executionContext.Actor,
            executionContext.CorrelationId,
            "track-identity.link",
            outcome,
            new
            {
                linkId,
                request.CanonicalRecordingId,
                conflictingCanonicalRecordingId,
                request.ExternalId.ProviderId,
                resourceKind = request.ExternalId.ResourceKind.ToString(),
                catalog = request.ExternalId.Catalog ?? DefaultCatalog,
                scope = request.Scope.ToString(),
                providerAccountId = request.Scope == ProviderIdentityScope.Account
                    ? executionContext.Account?.AccountId
                    : null,
                verification = request.Verification.ToString(),
                request.VerificationMethod,
                request.DecisionVersion
            });
    }

    private static void AddAudit(
        AllstarrDbContext context,
        ProviderActorContext actor,
        string correlationId,
        string action,
        string outcome,
        object details)
    {
        context.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = actor.TenantId,
            ActorUserId = actor.UserId,
            Category = "track-identity",
            Action = action,
            Outcome = outcome,
            CorrelationId = correlationId,
            DetailsJson = JsonSerializer.Serialize(details),
            CreatedAt = DateTimeOffset.UtcNow
        });
    }

    private sealed record ExactTrackIdentityKey(
        string ProviderId,
        ProviderResourceKind ResourceKind,
        string Catalog,
        string ExternalIdHash);
}
