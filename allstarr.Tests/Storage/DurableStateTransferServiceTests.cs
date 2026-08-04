using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Capabilities;
using allstarr.Core.Configuration;
using allstarr.Core.Favorites;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Downloads;
using allstarr.Core.Intelligence;
using allstarr.Core.Jobs;
using allstarr.Core.Playback;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Core.Settings;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class DurableStateTransferServiceTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "allstarr-tests",
        Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, PostgresTestDatabase> _databases = [];
    private TestDbContextFactory _sourceFactory = null!;
    private DurableStateTransferService _service = null!;
    private string _currentSchema = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var sourceDatabase = await PostgresTestDatabase.CreateAsync();
        _databases["source"] = sourceDatabase;
        var options = new DurableStorageOptions
        {
            Provider = "Postgres",
            ConnectionString = sourceDatabase.ConnectionString,
            BackupDirectory = Path.Combine(_root, "backups")
        };
        _sourceFactory = new TestDbContextFactory(sourceDatabase.Options);
        await using var context = await _sourceFactory.CreateDbContextAsync();
        _currentSchema = context.Database.GetMigrations().Last();
        var tenantId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var secretId = Guid.CreateVersion7();
        var intakeSecretId = Guid.CreateVersion7();
        var accountId = Guid.CreateVersion7();
        var canonicalRecordingId = Guid.CreateVersion7();
        var providerIdentityId = Guid.CreateVersion7();
        var backendIdentityId = Guid.CreateVersion7();
        var libraryTrackId = Guid.CreateVersion7();
        var externalSnapshotId = Guid.CreateVersion7();
        var scheduleId = Guid.CreateVersion7();
        var playlistLinkId = Guid.CreateVersion7();
        var playlistSnapshotId = Guid.CreateVersion7();
        var sourceEntryId = Guid.CreateVersion7();
        var matchId = Guid.CreateVersion7();
        var syncRunId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        var favoriteEventId = Guid.CreateVersion7();
        var managedFileId = Guid.CreateVersion7();
        var enrichmentPlanId = Guid.CreateVersion7();
        var downloadWorkspaceId = Guid.CreateVersion7();
        var downloadArtifactId = Guid.CreateVersion7();
        var intelligenceJobId = Guid.CreateVersion7();
        var playbackJobId = Guid.CreateVersion7();
        var recommendationRunId = Guid.CreateVersion7();
        var generatedSetId = Guid.CreateVersion7();
        var directGeneratedSetId = Guid.CreateVersion7();
        var routeDecisionId = Guid.CreateVersion7();
        var extensionPackageId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var stableHash = HashExternalId("phase4-transfer");
        context.Tenants.Add(new TenantRecord
        {
            Id = tenantId,
            Slug = "transfer",
            Name = "Transfer tenant",
            CreatedAt = DateTimeOffset.UtcNow
        });
        context.Users.Add(new PlatformUserRecord
        {
            Id = userId,
            TenantId = tenantId,
            DisplayName = "Transfer user",
            Status = PlatformUserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        context.SecretReferences.Add(new SecretReferenceRecord
        {
            Id = secretId,
            TenantId = tenantId,
            Purpose = "fixture.encrypted",
            ActiveVersion = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        context.SecretVersions.Add(new SecretVersionRecord
        {
            Id = Guid.CreateVersion7(),
            SecretReferenceId = secretId,
            Version = 1,
            KeyId = "external-key-1",
            Nonce = [1, 2, 3],
            Ciphertext = [9, 8, 7, 6],
            AuthenticationTag = [4, 5, 6],
            CreatedAt = DateTimeOffset.UtcNow
        });
        context.SecretReferences.Add(new SecretReferenceRecord
        {
            Id = intakeSecretId,
            TenantId = tenantId,
            Purpose = "listening-intake-token",
            ActiveVersion = 1,
            CreatedAt = now,
            UpdatedAt = now
        });
        context.SecretVersions.Add(new SecretVersionRecord
        {
            Id = Guid.CreateVersion7(),
            SecretReferenceId = intakeSecretId,
            Version = 1,
            KeyId = "external-key-1",
            Nonce = [7, 8, 9],
            Ciphertext = [1, 3, 5, 7],
            AuthenticationTag = [2, 4, 6],
            CreatedAt = now
        });
        context.ProviderAccounts.Add(new ProviderAccountRecord
        {
            Id = accountId,
            TenantId = tenantId,
            OwnerUserId = userId,
            ProviderId = "fixture",
            DisplayName = "Fixture account",
            Scope = ProviderAccountScope.User,
            SecretReferenceId = secretId,
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        context.CanonicalRecordings.Add(new CanonicalRecordingRecord
        {
            Id = canonicalRecordingId,
            TenantId = tenantId,
            CreatedByUserId = userId,
            Isrc = "USRC17607839",
            MusicBrainzRecordingId = "0d34fc3f-4f36-4b8d-a0d3-e0d7a8b6ff23",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Revision = 1
        });
        context.ProviderTrackIdentities.Add(new ProviderTrackIdentityRecord
        {
            Id = providerIdentityId,
            TenantId = tenantId,
            CanonicalRecordingId = canonicalRecordingId,
            ProviderAccountId = accountId,
            ProviderId = "fixture",
            ResourceKind = ProviderResourceKind.Track,
            CatalogNamespace = "default",
            Scope = ProviderIdentityScope.Account,
            ExternalId = "fixture-track-42",
            ExternalIdHash = HashExternalId("fixture-track-42"),
            Verification = ProviderIdentityVerification.Pinned,
            VerificationMethod = "fixture",
            DecisionVersion = 1,
            VerifiedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Revision = 1
        });
        context.Jobs.Add(new DurableJobRecord
        {
            Id = jobId,
            ScopeKey = tenantId.ToString("N"),
            TenantId = tenantId,
            OwnerUserId = userId,
            LibraryScopeId = "music",
            Type = "fixture.transfer",
            PayloadJson = "{\"secretReferenceId\":\"fixture\"}",
            IdempotencyKey = "transfer-1",
            State = DurableJobState.Pending,
            MaxAttempts = 3,
            AvailableAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        context.Jobs.Add(new DurableJobRecord
        {
            Id = playbackJobId,
            ScopeKey = $"user:{tenantId:N}:{userId:N}",
            TenantId = tenantId,
            OwnerUserId = userId,
            LibraryScopeId = "music",
            PolicySnapshotJson = "{}",
            RequestFingerprint = new string('e', 64),
            CorrelationId = "playback-transfer",
            Type = "playback.signal",
            PayloadJson = "{}",
            IdempotencyKey = "playback-transfer-signal",
            State = DurableJobState.Succeeded,
            Priority = 0,
            MaxAttempts = 3,
            MaxDeferrals = 3,
            AvailableAt = now,
            StartedAt = now,
            CompletedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });
        await context.SaveChangesAsync();
        context.ProviderRouteDecisions.Add(new ProviderRouteDecisionEntity
        {
            Id = routeDecisionId,
            TenantId = tenantId,
            ActorUserId = userId,
            DurableJobId = jobId,
            RouteKey = new string('7', 64),
            OperationId = "favorite-download",
            CorrelationId = "phase6-transfer",
            Capability = ProviderCapabilityKind.Download,
            LibraryScopeId = "music",
            SelectedProviderId = "fixture",
            SelectedProviderAccountId = accountId,
            CandidateDecisionsJson = JsonSerializer.Serialize(
                new[] { new ProviderRouteCandidateDecision("fixture", accountId,
                    ProviderRouteDecisionStatus.Accepted, "selected", 0) },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CreatedAt = now
        });
        context.ProviderRouteOutcomes.Add(new ProviderRouteOutcomeEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            RouteDecisionId = routeDecisionId,
            OutcomeKey = new string('8', 64),
            Sequence = 0,
            Stage = "download",
            ProviderId = "fixture",
            ProviderAccountId = accountId,
            Status = ProviderRouteOutcomeStatus.Succeeded,
            ReasonCode = "download-verified",
            CreatedAt = now
        });
        await context.SaveChangesAsync();
        context.FavoriteEvents.Add(new FavoriteEventRecord
        {
            Id = favoriteEventId,
            TenantId = tenantId,
            OwnerUserId = userId,
            Protocol = "jellyfin",
            BackendInstanceId = "transfer-backend",
            BackendPrincipalId = "transfer-principal",
            LibraryScopeId = "music",
            ItemId = "favorite-track-42",
            Operation = FavoriteOperation.Favorite,
            SourceRevision = "favorite-rev-1",
            EventKey = new string('e', 64),
            CorrelationId = "phase6-transfer",
            PolicySnapshotJson = "{\"version\":1}",
            JobId = jobId,
            State = FavoriteEventState.Succeeded,
            CreatedAt = now,
            UpdatedAt = now,
            CompletedAt = now
        });
        context.ManagedFiles.Add(new ManagedFileOwnershipEntity
        {
            Id = managedFileId,
            RootId = Guid.CreateVersion7(),
            TargetRootPath = "/managed/music",
            CanonicalPath = "/managed/music/Fixture/Transfer.flac",
            ContentSha256 = new string('a', 64),
            Length = 1234,
            FileSystemDeviceId = "2a",
            FileSystemFileId = "3b",
            FileSystemLinkCount = 1,
            PlacementMethod = ManagedFilePlacementMethod.Copy,
            TenantId = tenantId,
            OwnerUserId = userId,
            LibraryScopeId = "music",
            SourceJobId = jobId,
            ScopeKey = "user:transfer",
            ReferenceCount = 1,
            IsManaged = true,
            CreatedAt = now
        });
        context.ManagedFileReferences.Add(new ManagedFileReferenceEntity
        {
            Id = Guid.CreateVersion7(),
            ManagedFileId = managedFileId,
            TenantId = tenantId,
            OwnerUserId = userId,
            ScopeKey = "user:transfer",
            ReferenceKey = "favorite:transfer",
            CreatedAt = now,
            Revision = 1
        });
        context.ProviderDownloadWorkspaces.Add(new ProviderDownloadWorkspaceEntity
        {
            Id = downloadWorkspaceId,
            WorkspaceId = new string('c', 64),
            TenantId = tenantId,
            OwnerUserId = userId,
            LibraryScopeId = "music",
            DurableJobId = jobId,
            ProviderId = "fixture",
            ProviderAccountId = accountId,
            IdempotencyKey = "phase6-download-workspace",
            CreatedAt = now,
            CompletedAt = now,
            Revision = 1
        });
        // Persist the managed parent before the placed artifact so PostgreSQL's
        // database-native lineage trigger can validate the child insert.
        await context.SaveChangesAsync();
        context.ProviderDownloadArtifacts.Add(new ProviderDownloadArtifactEntity
        {
            Id = downloadArtifactId,
            WorkspaceRecordId = downloadWorkspaceId,
            WorkspaceId = new string('c', 64),
            TenantId = tenantId,
            OwnerUserId = userId,
            LibraryScopeId = "music",
            DurableJobId = jobId,
            ProviderId = "fixture",
            ProviderAccountId = accountId,
            ProviderArtifactId = "provider/output.flac",
            RelativePath = "provider/output.flac",
            ContentSha256 = new string('d', 64),
            Length = 4321,
            State = ProviderDownloadArtifactState.Placed,
            ManagedFileId = managedFileId,
            CreatedAt = now,
            VerifiedAt = now,
            PlacedAt = now,
            Revision = 1
        });
        await context.SaveChangesAsync();
        context.FavoriteActions.Add(new FavoriteActionRecord
        {
            Id = Guid.CreateVersion7(),
            EventId = favoriteEventId,
            TenantId = tenantId,
            OwnerUserId = userId,
            ActionType = "enrich",
            IdempotencyKey = "phase6-action-1",
            State = FavoriteActionState.Succeeded,
            AttemptCount = 1,
            CreatedAt = now,
            UpdatedAt = now,
            CompletedAt = now
        });
        context.FavoriteStates.Add(new FavoriteStateRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            OwnerUserId = userId,
            Protocol = "jellyfin",
            BackendInstanceId = "transfer-backend",
            ItemId = "favorite-track-42",
            IsFavorite = true,
            LastEventId = favoriteEventId,
            UpdatedAt = now
        });
        context.MetadataEnrichmentPlans.Add(new MetadataEnrichmentPlanRecord
        {
            Id = enrichmentPlanId,
            TenantId = tenantId,
            OwnerUserId = userId,
            LineageJobId = jobId,
            ManagedArtifactId = managedFileId,
            Fingerprint = new string('b', 64),
            PlanVersion = 1,
            SourceRevisionsJson = "[\"musicbrainz:rev-1\"]",
            DecisionsJson = "[{\"field\":\"title\"}]",
            TagsJson = "{\"title\":\"Transfer\"}",
            PathValuesJson = "{\"artist\":\"Fixture\"}",
            CreatedAt = now
        });
        await context.SaveChangesAsync();
        context.MetadataEnrichmentApplications.Add(new MetadataEnrichmentApplicationRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            OwnerUserId = userId,
            PlanId = enrichmentPlanId,
            ManagedArtifactId = managedFileId,
            LineageJobId = jobId,
            ArtifactContentSha256 = new string('a', 64),
            State = MetadataEnrichmentApplicationState.Applied,
            CreatedAt = now,
            UpdatedAt = now
        });
        await context.SaveChangesAsync();
        context.BackendIdentities.Add(new BackendIdentityRecord
        {
            Id = backendIdentityId,
            TenantId = tenantId,
            UserId = userId,
            BackendType = "jellyfin",
            BackendInstanceId = "transfer-backend",
            PrincipalId = "transfer-principal",
            CreatedAt = now,
            LastSeenAt = now
        });
        context.FavoriteActionPolicies.AddRange(
            new FavoriteActionPolicyRecord
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                Scope = FavoriteActionPolicyScope.Global,
                Protocol = "jellyfin",
                BackendInstanceId = "transfer-backend",
                LibraryScopeId = "music",
                AddToVirtualLiked = true,
                MatchLocalLibrary = false,
                AutoDownload = false,
                EnrichMetadata = false,
                PlaceManagedFile = false,
                RefreshBackendLibrary = true,
                UpdatedByUserId = userId,
                CreatedAt = now,
                UpdatedAt = now,
                Revision = 1
            },
            new FavoriteActionPolicyRecord
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                OwnerUserId = userId,
                Scope = FavoriteActionPolicyScope.User,
                Protocol = "jellyfin",
                BackendInstanceId = "transfer-backend",
                LibraryScopeId = "music",
                AutoDownload = true,
                RefreshBackendLibrary = false,
                UpdatedByUserId = userId,
                CreatedAt = now,
                UpdatedAt = now,
                Revision = 1
            });
        context.LibraryTracks.Add(new LibraryTrackRecord
        {
            Id = libraryTrackId,
            TenantId = tenantId,
            OwnerUserId = userId,
            BackendIdentityId = backendIdentityId,
            CanonicalRecordingId = canonicalRecordingId,
            LibraryScopeId = "music",
            Protocol = "jellyfin",
            BackendInstanceId = "transfer-backend",
            BackendItemId = "local-42",
            FilePath = "/media/Music/Transfer.flac",
            Title = "Transfer",
            Artist = "Fixture",
            DurationMilliseconds = 123000,
            ProviderIdsJson = "{}",
            IndexedAt = now,
            SourceModifiedAt = now,
            UpdatedAt = now
        });
        context.Jobs.Add(new DurableJobRecord
        {
            Id = intelligenceJobId,
            ScopeKey = $"user:{tenantId:N}:{userId:N}",
            TenantId = tenantId,
            OwnerUserId = userId,
            LibraryScopeId = "music",
            PolicySnapshotJson = "{}",
            RequestFingerprint = new string('c', 64),
            CorrelationId = "intelligence-transfer",
            Type = "recommendation.generate",
            PayloadJson = $"{{\"RunId\":\"{recommendationRunId}\"}}",
            IdempotencyKey = "intelligence-transfer-run",
            State = DurableJobState.Succeeded,
            Priority = 0,
            MaxAttempts = 3,
            MaxDeferrals = 3,
            AvailableAt = now,
            StartedAt = now,
            CompletedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });
        context.IntelligencePolicies.Add(new IntelligencePolicyRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            OwnerUserId = userId,
            Protocol = "jellyfin",
            BackendInstanceId = "transfer-backend",
            LibraryScopeId = "music",
            Enabled = true,
            RetentionDays = 30,
            AllowedSignalTypesJson = "[\"favorite\",\"play\"]",
            EnabledProvidersJson = "[\"local-rules\"]",
            CreatedAt = now,
            UpdatedAt = now,
            Revision = 1
        });
        context.ListeningIntakeTokens.Add(new ListeningIntakeTokenRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            OwnerUserId = userId,
            Protocol = "jellyfin",
            BackendInstanceId = "transfer-backend",
            LibraryScopeId = "music",
            SecretReferenceId = intakeSecretId,
            RelayExternally = false,
            CreatedAt = now
        });
        context.ListeningEvents.Add(new ListeningEventRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            OwnerUserId = userId,
            Protocol = "jellyfin",
            BackendInstanceId = "transfer-backend",
            LibraryScopeId = "music",
            OccurrenceKey = new string('9', 64),
            State = ListeningEventState.Completed,
            StartedAt = now,
            ListenedAt = now,
            UpdatedAt = now,
            PositionTicks = TimeSpan.FromSeconds(62).Ticks,
            DurationMilliseconds = 123_000,
            ClientClass = "fixture-client",
            DeviceClass = "fixture-device",
            SourceKind = "protocol",
            TrackReference = "local-42",
            Title = "Transfer",
            Artist = "Fixture",
            AlbumArtist = "Fixture album artist",
            RecordingMusicBrainzId = "11111111-1111-1111-1111-111111111111",
            Isrc = "USABC1234567",
            MusicBrainzEnrichmentState = MusicBrainzEnrichmentState.Resolved,
            MusicBrainzEnrichmentConfidence = .98,
            MusicBrainzSourceRevision = "musicbrainz:ws2",
            MusicBrainzFactsJson = "{\"id\":\"11111111-1111-1111-1111-111111111111\"}",
            MusicBrainzEnrichedAt = now,
            TrackNumber = 3,
            CanonicalRecordingId = canonicalRecordingId,
            LibraryTrackId = libraryTrackId
        });
        context.ListeningHistoryImports.Add(new ListeningHistoryImportRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            OwnerUserId = userId,
            Protocol = "jellyfin",
            BackendInstanceId = "transfer-backend",
            LibraryScopeId = "music",
            DisplayFileName = "StreamingHistory_music_0.json",
            Format = "spotify-extended-streaming-history",
            ContentSha256 = new string('a', 64),
            SizeBytes = 1234,
            PreviewJson = JsonSerializer.Serialize(new ListeningHistoryImportPreview(
                "spotify-extended-streaming-history", 2, 2, 1, 1, 0, 0, 0, 0, 0, 0, 2, 1, 1, 0, 1, 1,
                now.AddDays(-2), now.AddDays(-1), new Dictionary<string, long> { ["completed"] = 1 })),
            PreviewRevision = new string('b', 64),
            State = ListeningHistoryImportState.Previewed,
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = now.AddHours(24),
            Revision = 1
        });
        context.ListeningSignals.Add(new ListeningSignalRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            OwnerUserId = userId,
            Protocol = "jellyfin",
            BackendInstanceId = "transfer-backend",
            LibraryScopeId = "music",
            SignalType = "favorite",
            TrackKeyHash = new string('d', 64),
            TrackReference = $"library:{libraryTrackId:N}",
            Value = 1,
            SignalKey = new string('f', 64),
            SourceJobId = playbackJobId,
            ObservedAt = now,
            ExpiresAt = now.AddDays(30)
        });
        context.PlaybackDeliveryCheckpoints.Add(new PlaybackDeliveryCheckpointEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            OwnerUserId = userId,
            OccurrenceKey = new string('9', 64),
            SignalKey = new string('f', 64),
            TargetId = "lastfm",
            Kind = PlaybackScrobbleDeliveryKind.Completed,
            State = ScopedPlaybackScrobbleOutcome.Delivered,
            DetailsJson = "{\"accepted\":1}",
            UpdatedAt = now
        });
        var profile = new ListeningProfile(tenantId, userId, "transfer-backend", "music", 1, 0, 1,
            new Dictionary<string, double>(), now, now)
        { TopTrackKeys = [$"library:{libraryTrackId:N}"] };
        context.ListeningProfiles.Add(new ListeningProfileRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            OwnerUserId = userId,
            Protocol = "jellyfin",
            BackendInstanceId = "transfer-backend",
            LibraryScopeId = "music",
            ProfileJson = JsonSerializer.Serialize(profile),
            WindowStart = now,
            WindowEnd = now,
            CreatedAt = now
        });
        context.RecommendationRuns.Add(new RecommendationRunRecord
        {
            Id = recommendationRunId,
            TenantId = tenantId,
            OwnerUserId = userId,
            Protocol = "jellyfin",
            BackendInstanceId = "transfer-backend",
            LibraryScopeId = "music",
            JobId = intelligenceJobId,
            IdempotencyKey = "intelligence-transfer-run",
            PolicySnapshotJson = JsonSerializer.Serialize(
                new RecommendationPolicySnapshot(1, ["local-rules"], 30)),
            SeedTrackKeysJson = "[]",
            Limit = 10,
            State = RecommendationRunState.Succeeded,
            CreatedAt = now,
            UpdatedAt = now,
            CompletedAt = now,
            Revision = 1
        });
        var identityJson = JsonSerializer.Serialize(new RecommendationTrackIdentity("local", LibraryTrackId: libraryTrackId, BackendItemId: "local-42"));
        var signalsJson = JsonSerializer.Serialize(new[] { new RecommendationSignal("shared-artist", .8, "Shares an artist.") });
        var recommendationCandidateId = Guid.CreateVersion7();
        context.RecommendationCandidates.Add(new RecommendationCandidateRecord
        {
            Id = recommendationCandidateId,
            RunId = recommendationRunId,
            TenantId = tenantId,
            OwnerUserId = userId,
            Position = 0,
            TrackKey = "local-42",
            Score = .8,
            Source = "local-rules",
            SignalsJson = signalsJson,
            IdentityJson = identityJson,
            CreatedAt = now
        });
        context.RecommendationFeedback.Add(new RecommendationFeedbackRecord
        {
            Id = Guid.CreateVersion7(),
            CandidateId = recommendationCandidateId,
            TenantId = tenantId,
            OwnerUserId = userId,
            Protocol = "jellyfin",
            BackendInstanceId = "transfer-backend",
            LibraryScopeId = "music",
            TrackKey = "local-42",
            Kind = "like",
            ReasonCode = "great-fit",
            CreatedAt = now,
            UpdatedAt = now,
            Revision = 1
        });
        context.GeneratedSets.Add(new GeneratedSetRecord
        {
            Id = generatedSetId,
            RunId = recommendationRunId,
            TenantId = tenantId,
            OwnerUserId = userId,
            Protocol = "jellyfin",
            BackendInstanceId = "transfer-backend",
            LibraryScopeId = "music",
            Name = "Transfer mix",
            MaterializationState = GeneratedSetMaterializationState.Pending,
            CreatedAt = now,
            UpdatedAt = now,
            Revision = 1
        });
        context.GeneratedSetEntries.Add(new GeneratedSetEntryRecord
        {
            Id = Guid.CreateVersion7(),
            GeneratedSetId = generatedSetId,
            TenantId = tenantId,
            OwnerUserId = userId,
            Position = 0,
            TrackKey = "local-42",
            Score = .8,
            Source = "local-rules",
            ExplanationJson = signalsJson,
            IdentityJson = identityJson
        });
        context.GeneratedSets.Add(new GeneratedSetRecord
        {
            Id = directGeneratedSetId,
            TenantId = tenantId,
            OwnerUserId = userId,
            Protocol = "jellyfin",
            BackendInstanceId = "transfer-backend",
            LibraryScopeId = "music",
            Name = "Sound preview",
            MaterializationState = GeneratedSetMaterializationState.Pending,
            CreatedAt = now,
            UpdatedAt = now,
            Revision = 1
        });
        context.GeneratedSetEntries.Add(new GeneratedSetEntryRecord
        {
            Id = Guid.CreateVersion7(),
            GeneratedSetId = directGeneratedSetId,
            TenantId = tenantId,
            OwnerUserId = userId,
            Position = 0,
            TrackKey = "local-42",
            Score = .9,
            Source = "audiomuse-ai",
            ExplanationJson = JsonSerializer.Serialize(new[]
                { new RecommendationSignal("audiomuse-preview", 1, "Selected preview.") }),
            IdentityJson = identityJson
        });
        context.ExternalMetadataSnapshots.Add(new ExternalMetadataSnapshotRecord
        {
            Id = externalSnapshotId,
            TenantId = tenantId,
            OwnerUserId = userId,
            ProviderAccountId = accountId,
            ProviderTrackIdentityId = providerIdentityId,
            LibraryScopeId = "music",
            BackendInstanceId = "transfer-backend",
            BackendPrincipalId = "transfer-principal",
            Protocol = "jellyfin",
            ProviderId = "fixture",
            ResourceKind = "track",
            ExternalIdHash = stableHash,
            SnapshotVersion = 1,
            ProviderRevision = "track-rev-1",
            PayloadJson = "{\"title\":\"Transfer\"}",
            PayloadSha256 = stableHash,
            CorrelationId = "phase4-transfer",
            RetrievedAt = now
        });
        await context.SaveChangesAsync();
        context.TrackMatches.Add(new TrackMatchRecord
        {
            Id = matchId,
            TenantId = tenantId,
            OwnerUserId = userId,
            ExternalSnapshotId = externalSnapshotId,
            LibraryTrackId = libraryTrackId,
            CanonicalRecordingId = canonicalRecordingId,
            LibraryScopeId = "music",
            State = TrackMatchState.Accepted,
            Confidence = .99,
            Threshold = .85,
            DecisionVersion = 1,
            PolicyVersion = "match-v1",
            CandidateResultsJson = "[]",
            ReasonsJson = "[\"exact-id\"]",
            WarningsJson = "[]",
            CorrelationId = "phase4-transfer",
            DecidedAt = now
        });
        context.ManualTrackOverrides.Add(new ManualTrackOverrideRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            OwnerUserId = userId,
            ExternalSnapshotId = externalSnapshotId,
            LibraryTrackId = libraryTrackId,
            LibraryScopeId = "music",
            Decision = ManualOverrideDecision.Pin,
            Reason = "transfer fixture",
            DecisionVersion = 1,
            CreatedAt = now
        });
        context.JobSchedules.Add(new JobScheduleRecord
        {
            Id = scheduleId,
            TenantId = tenantId,
            OwnerUserId = userId,
            LibraryScopeId = "music",
            JobType = "playlist-sync",
            CronExpression = "0 0 * * *",
            TimeZoneId = "UTC",
            OverlapPolicy = ScheduleOverlapPolicy.Skip,
            MisfirePolicy = ScheduleMisfirePolicy.RunOnce,
            Enabled = true,
            NextRunAt = now.AddDays(1),
            CreatedAt = now,
            UpdatedAt = now
        });
        await context.SaveChangesAsync();
        context.PlaylistLinks.Add(new PlaylistLinkRecord
        {
            Id = playlistLinkId,
            TenantId = tenantId,
            OwnerUserId = userId,
            ProviderAccountId = accountId,
            ScheduleId = scheduleId,
            LibraryScopeId = "music",
            SourceProviderId = "fixture",
            SourcePlaylistId = "playlist-42",
            SourcePlaylistIdHash = stableHash,
            TargetProtocol = "jellyfin",
            TargetBackendInstanceId = "transfer-backend",
            Mode = PlaylistLinkMode.Materialized,
            MaterializationMode = PlaylistMaterializationMode.Reconcile,
            RuleVersion = "rules-v1",
            PolicyVersion = "policy-v1",
            CreatedAt = now,
            UpdatedAt = now
        });
        await context.SaveChangesAsync();
        context.PlaylistSourceSnapshots.Add(new PlaylistSourceSnapshotRecord
        {
            Id = playlistSnapshotId,
            TenantId = tenantId,
            OwnerUserId = userId,
            PlaylistLinkId = playlistLinkId,
            ProviderAccountId = accountId,
            SnapshotVersion = 1,
            ProviderRevision = "playlist-rev-1",
            Name = "Transfer playlist",
            ArtworkReferenceKey = "fixture:art:42",
            PayloadSha256 = stableHash,
            CorrelationId = "phase4-transfer",
            RetrievedAt = now
        });
        await context.SaveChangesAsync();
        context.PlaylistSourceEntries.Add(new PlaylistSourceEntryRecord
        {
            Id = sourceEntryId,
            TenantId = tenantId,
            PlaylistSourceSnapshotId = playlistSnapshotId,
            ExternalMetadataSnapshotId = externalSnapshotId,
            SourcePosition = 0,
            SourceEntryIdHash = stableHash
        });
        await context.SaveChangesAsync();
        context.PlaylistSyncRuns.Add(new PlaylistSyncRunRecord
        {
            Id = syncRunId,
            TenantId = tenantId,
            OwnerUserId = userId,
            PlaylistLinkId = playlistLinkId,
            PlaylistSourceSnapshotId = playlistSnapshotId,
            ScheduleId = scheduleId,
            Generation = 1,
            IdempotencyKey = "phase4-transfer-run",
            RuleVersion = "rules-v1",
            MaterializationMode = PlaylistMaterializationMode.Reconcile,
            State = PlaylistSyncState.Succeeded,
            StartedAt = now,
            CompletedAt = now
        });
        await context.SaveChangesAsync();
        context.PlaylistSyncEntryResults.Add(new PlaylistSyncEntryResultRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            PlaylistSyncRunId = syncRunId,
            PlaylistSourceEntryId = sourceEntryId,
            TrackMatchId = matchId,
            LibraryTrackId = libraryTrackId,
            SourcePosition = 0,
            TargetPosition = 0,
            Outcome = PlaylistEntryOutcome.Reused
        });
        context.PlaylistTargetMemberships.Add(new PlaylistTargetMembershipRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            PlaylistLinkId = playlistLinkId,
            LibraryTrackId = libraryTrackId,
            CreatedBySyncRunId = syncRunId,
            TargetEntryId = "target-entry-42",
            LastKnownPosition = 0,
            Active = true,
            CreatedAt = now,
            UpdatedAt = now
        });
        context.TenantRuntimeSettings.Add(new TenantRuntimeSettingRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Key = AudioQualityPolicy.SettingKey,
            ValueType = RuntimeSettingValueType.String,
            ValueJson = "\"HiResLossless\"",
            Source = "v3-compatibility-migration",
            UpdatedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
            Revision = 2
        });
        context.ExtensionPackages.Add(new ExtensionPackageRecord
        {
            Id = extensionPackageId,
            ExtensionId = "spotiflac-transfer",
            DisplayName = "Transfer extension",
            Version = "1.0.0",
            SdkVersion = "1",
            Sha256 = new string('c', 64),
            ContentSha256 = new string('d', 64),
            PackagePath = "/extensions/spotiflac-transfer",
            ManifestJson = """{"id":"spotiflac-transfer","compatibility":"spotiflac-v1"}""",
            State = ExtensionPackageState.Active,
            StagedAt = now,
            ActivatedAt = now,
            Revision = 1
        });
        context.ExtensionLogs.Add(new ExtensionLogRecord
        {
            Id = Guid.CreateVersion7(),
            ExtensionPackageId = extensionPackageId,
            ExtensionId = "spotiflac-transfer",
            Level = "Info",
            EventCode = "transfer",
            Message = "Transfer fixture",
            CorrelationId = "transfer-extension",
            CreatedAt = now
        });
        context.OnboardingStates.Add(new OnboardingStateRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            UserId = userId,
            SchemaVersion = OnboardingStateService.SchemaVersion,
            CompletedStepsJson = """["backend-identity"]""",
            CompletionSource = "transfer-fixture",
            CompletedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            Revision = 1
        });
        var migrationAuditId = Guid.CreateVersion7();
        const string migrationSource = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var migrationResult = new LegacyEnvMigrationApplyResult(
            true, false, 1, 0, 0, 0, 0, 0, [], migrationSource, now);
        context.AuditEvents.Add(new AuditEventRecord
        {
            Id = migrationAuditId,
            TenantId = tenantId,
            ActorUserId = userId,
            Category = "configuration-migration",
            Action = "legacy-env.apply",
            Outcome = "succeeded",
            CorrelationId = "transfer-legacy-env",
            DetailsJson = "{}",
            CreatedAt = now
        });
        context.LegacyEnvImports.Add(new LegacyEnvImportRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            SourceSha256 = migrationSource,
            ActorUserId = userId,
            AuditEventId = migrationAuditId,
            ResultJson = JsonSerializer.Serialize(migrationResult, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            AppliedAt = now
        });
        await context.SaveChangesAsync();
        var state = new DurableStorageState(options);
        state.Set(DurableStorageReadiness.Ready, _currentSchema);
        _service = new DurableStateTransferService(_sourceFactory, options, state);
    }

    [Fact]
    public async Task ExportImport_PreservesTenantScopedStateTrackIdentitiesAndEncryptedSecretBytes()
    {
        const string temporaryUploadCanary = "private-history-upload-must-not-transfer";
        const string remoteTokenCanary = "remote-history-token-must-not-transfer";
        await File.WriteAllTextAsync(
            Path.Combine(_root, "history-import.upload"),
            $"{temporaryUploadCanary}|{remoteTokenCanary}");
        var artifact = await _service.ExportAsync(
            Path.Combine(_root, "transfers"),
            writesQuiesced: true);
        Assert.Equal(_currentSchema, artifact.SchemaVersion);
        using (var archive = ZipFile.OpenRead(artifact.Path))
        {
            Assert.Contains(archive.Entries, item => item.FullName == "listening-events.json");
            Assert.Contains(archive.Entries, item => item.FullName == "listening-history-imports.json");
            var contents = new List<string>();
            foreach (var entry in archive.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                contents.Add(await reader.ReadToEndAsync());
            }
            var archiveText = string.Join('\n', contents);
            Assert.DoesNotContain(temporaryUploadCanary, archiveText, StringComparison.Ordinal);
            Assert.DoesNotContain(remoteTokenCanary, archiveText, StringComparison.Ordinal);
        }
        var targetPath = Path.Combine(_root, "target.db");
        var targetFactory = Factory($"postgres-fixture:{targetPath}");

        await DurableStateTransferService.ImportAsync(
            artifact,
            targetFactory,
            targetConfirmedEmpty: true);

        await using var target = await targetFactory.CreateDbContextAsync();
        Assert.Single(await target.Tenants.ToListAsync());
        Assert.Single(await target.Users.ToListAsync());
        Assert.Single(await target.ProviderAccounts.ToListAsync());
        var runtimeSetting = await target.TenantRuntimeSettings.SingleAsync();
        Assert.Equal(AudioQualityPolicy.SettingKey, runtimeSetting.Key);
        Assert.Equal("\"HiResLossless\"", runtimeSetting.ValueJson);
        Assert.Equal(2, runtimeSetting.Revision);
        Assert.Equal("spotiflac-transfer", (await target.ExtensionPackages.SingleAsync()).ExtensionId);
        Assert.Equal("spotiflac-transfer", (await target.ExtensionLogs.SingleAsync()).ExtensionId);
        var onboarding = await target.OnboardingStates.SingleAsync();
        Assert.Equal(OnboardingStateService.SchemaVersion, onboarding.SchemaVersion);
        Assert.NotNull(onboarding.CompletedAt);
        var legacyImport = await target.LegacyEnvImports.SingleAsync();
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", legacyImport.SourceSha256);
        Assert.Equal((await target.AuditEvents.SingleAsync()).Id, legacyImport.AuditEventId);
        var canonicalRecording = await target.CanonicalRecordings.SingleAsync();
        Assert.Equal("USRC17607839", canonicalRecording.Isrc);
        var trackIdentity = await target.ProviderTrackIdentities.SingleAsync();
        Assert.Equal(canonicalRecording.Id, trackIdentity.CanonicalRecordingId);
        Assert.Equal("fixture-track-42", trackIdentity.ExternalId);
        Assert.Equal(ProviderIdentityScope.Account, trackIdentity.Scope);
        Assert.Equal(ProviderIdentityVerification.Pinned, trackIdentity.Verification);
        Assert.Equal(3, await target.Jobs.CountAsync());
        Assert.Single(await target.LibraryTracks.ToListAsync());
        Assert.Single(await target.ExternalMetadataSnapshots.ToListAsync());
        Assert.Equal(TrackMatchState.Accepted, (await target.TrackMatches.SingleAsync()).State);
        Assert.Equal(ManualOverrideDecision.Pin, (await target.ManualTrackOverrides.SingleAsync()).Decision);
        Assert.Single(await target.JobSchedules.ToListAsync());
        Assert.Single(await target.PlaylistLinks.ToListAsync());
        Assert.Equal("Transfer playlist", (await target.PlaylistSourceSnapshots.SingleAsync()).Name);
        Assert.Single(await target.PlaylistSourceEntries.ToListAsync());
        Assert.Equal(PlaylistSyncState.Succeeded, (await target.PlaylistSyncRuns.SingleAsync()).State);
        Assert.Equal(PlaylistEntryOutcome.Reused, (await target.PlaylistSyncEntryResults.SingleAsync()).Outcome);
        Assert.Single(await target.PlaylistTargetMemberships.ToListAsync());
        Assert.Equal(FavoriteEventState.Succeeded, (await target.FavoriteEvents.SingleAsync()).State);
        Assert.Equal(FavoriteActionState.Succeeded, (await target.FavoriteActions.SingleAsync()).State);
        Assert.True((await target.FavoriteStates.SingleAsync()).IsFavorite);
        var restoredPolicies = await target.FavoriteActionPolicies.OrderBy(item => item.Scope).ToListAsync();
        Assert.Equal(2, restoredPolicies.Count);
        Assert.Contains(restoredPolicies, item => item.Scope == FavoriteActionPolicyScope.Global && item.RefreshBackendLibrary == true);
        Assert.Contains(restoredPolicies, item => item.Scope == FavoriteActionPolicyScope.User && item.AutoDownload == true);
        var managedFile = await target.ManagedFiles.SingleAsync();
        Assert.Equal("/managed/music/Fixture/Transfer.flac", managedFile.CanonicalPath);
        Assert.True(managedFile.IsManaged);
        Assert.Equal("2a", managedFile.FileSystemDeviceId);
        var managedReference = await target.ManagedFileReferences.SingleAsync();
        Assert.Equal(managedFile.Id, managedReference.ManagedFileId);
        Assert.Equal("favorite:transfer", managedReference.ReferenceKey);
        Assert.Null(managedReference.ReleasedAt);
        var workspace = await target.ProviderDownloadWorkspaces.SingleAsync();
        Assert.Equal((await target.Jobs.SingleAsync(item => item.Type == "fixture.transfer")).Id, workspace.DurableJobId);
        var downloadArtifact = await target.ProviderDownloadArtifacts.SingleAsync();
        Assert.Equal(workspace.Id, downloadArtifact.WorkspaceRecordId);
        Assert.Equal(managedFile.Id, downloadArtifact.ManagedFileId);
        Assert.Equal(ProviderDownloadArtifactState.Placed, downloadArtifact.State);
        var enrichmentPlan = await target.MetadataEnrichmentPlans.SingleAsync();
        Assert.Equal(managedFile.Id, enrichmentPlan.ManagedArtifactId);
        Assert.Contains("musicbrainz:rev-1", enrichmentPlan.SourceRevisionsJson, StringComparison.Ordinal);
        Assert.Equal(MetadataEnrichmentApplicationState.Applied,
            (await target.MetadataEnrichmentApplications.SingleAsync()).State);
        Assert.True((await target.IntelligencePolicies.SingleAsync()).Enabled);
        var listeningIntake = await target.ListeningIntakeTokens.SingleAsync();
        Assert.False(listeningIntake.RelayExternally);
        Assert.Equal("listening-intake-token", (await target.SecretReferences.SingleAsync(item =>
            item.Id == listeningIntake.SecretReferenceId)).Purpose);
        var listeningEvent = await target.ListeningEvents.SingleAsync();
        Assert.Equal(ListeningEventState.Completed, listeningEvent.State);
        Assert.Equal("local-42", listeningEvent.TrackReference);
        Assert.Equal("USABC1234567", listeningEvent.Isrc);
        Assert.Equal(MusicBrainzEnrichmentState.Resolved, listeningEvent.MusicBrainzEnrichmentState);
        Assert.Equal(.98, listeningEvent.MusicBrainzEnrichmentConfidence);
        Assert.Contains("11111111-1111-1111-1111-111111111111", listeningEvent.MusicBrainzFactsJson);
        var listeningImport = await target.ListeningHistoryImports.SingleAsync();
        Assert.Equal(listeningEvent.TenantId, listeningImport.TenantId);
        Assert.Equal(listeningEvent.OwnerUserId, listeningImport.OwnerUserId);
        Assert.Equal(listeningEvent.Protocol, listeningImport.Protocol);
        Assert.Equal(listeningEvent.BackendInstanceId, listeningImport.BackendInstanceId);
        Assert.Equal(listeningEvent.LibraryScopeId, listeningImport.LibraryScopeId);
        Assert.Equal("spotify-extended-streaming-history", listeningImport.Format);
        Assert.Equal(new string('a', 64), listeningImport.ContentSha256);
        Assert.Equal(ListeningHistoryImportState.Expired, listeningImport.State);
        Assert.Null(listeningImport.JobId);
        Assert.StartsWith("library:", (await target.ListeningSignals.SingleAsync()).TrackReference, StringComparison.Ordinal);
        var playbackCheckpoint = await target.PlaybackDeliveryCheckpoints.SingleAsync();
        Assert.Equal("lastfm", playbackCheckpoint.TargetId);
        Assert.Equal(ScopedPlaybackScrobbleOutcome.Delivered, playbackCheckpoint.State);
        Assert.Single(await target.ListeningProfiles.ToListAsync());
        Assert.Equal(RecommendationRunState.Succeeded, (await target.RecommendationRuns.SingleAsync()).State);
        Assert.Contains("shared-artist", (await target.RecommendationCandidates.SingleAsync()).SignalsJson, StringComparison.Ordinal);
        Assert.Equal("great-fit", (await target.RecommendationFeedback.SingleAsync()).ReasonCode);
        Assert.Equal(["Sound preview", "Transfer mix"], await target.GeneratedSets
            .OrderBy(item => item.Name).Select(item => item.Name).ToArrayAsync());
        Assert.Equal(["audiomuse-ai", "local-rules"], await target.GeneratedSetEntries
            .OrderBy(item => item.Source).Select(item => item.Source).ToArrayAsync());
        var fixtureSecret = await target.SecretReferences.SingleAsync(item => item.Purpose == "fixture.encrypted");
        var secret = await target.SecretVersions.SingleAsync(item => item.SecretReferenceId == fixtureSecret.Id);
        Assert.Equal("external-key-1", secret.KeyId);
        Assert.Equal([9, 8, 7, 6], secret.Ciphertext);
        Assert.False(File.ReadAllText(artifact.Path).Contains("plaintext-secret", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("intelligence-policies.json", "retentionDays", 0)]
    [InlineData("intelligence-policies.json", "allowedSignalTypesJson", "[\"Play\"]")]
    [InlineData("intelligence-policies.json", "targetCredentialReferenceId", "00000000-0000-0000-0000-000000000001")]
    [InlineData("listening-events.json", "occurrenceKey", "short")]
    [InlineData("listening-events.json", "libraryTrackId", "00000000-0000-0000-0000-000000000001")]
    [InlineData("listening-signals.json", "ownerUserId", "00000000-0000-0000-0000-000000000001")]
    [InlineData("listening-signals.json", "trackReference", "library:00000000000000000000000000000001")]
    [InlineData("playback-delivery-checkpoints.json", "signalKey", "short")]
    [InlineData("playback-delivery-checkpoints.json", "targetId", "unsupported")]
    [InlineData("playback-delivery-checkpoints.json", "ownerUserId", "00000000-0000-0000-0000-000000000001")]
    [InlineData("listening-profiles.json", "profileJson", "{}")]
    [InlineData("recommendation-runs.json", "idempotencyKey", "")]
    [InlineData("recommendation-runs.json", "policySnapshotJson", "{}")]
    [InlineData("recommendation-runs.json", "state", 4)]
    [InlineData("recommendation-candidates.json", "position", 4)]
    [InlineData("recommendation-candidates.json", "signalsJson", "[]")]
    [InlineData("recommendation-candidates.json", "sourceRevision", "")]
    [InlineData("recommendation-candidates.json", "exclusionsJson", "{broken")]
    [InlineData("recommendation-feedback.json", "kind", "maybe")]
    [InlineData("generated-sets.json", "ownerUserId", "00000000-0000-0000-0000-000000000001")]
    [InlineData("generated-sets.json", "backendPlaylistId", "playlist-with-pending-state")]
    [InlineData("generated-sets.json", "revision", 0)]
    [InlineData("generated-set-entries.json", "position", 3)]
    [InlineData("generated-set-entries.json", "identityJson", "{broken")]
    public async Task Import_RejectsMalformedIntelligenceGraphBeforeAnyDatabaseWrite(
        string entryName, string property, object replacement)
    {
        var artifact = await _service.ExportAsync(Path.Combine(_root, $"bad-intelligence-{Guid.NewGuid():N}"), true);
        artifact = await RewriteJsonArrayEntryAsync(artifact, entryName, values =>
        {
            var item = values[0]!.AsObject();
            item[property] = replacement switch
            {
                int number => JsonValue.Create(number),
                string text => JsonValue.Create(text),
                _ => throw new InvalidOperationException()
            };
        });
        var targetFactory = Factory($"postgres-fixture:{Path.Combine(_root, $"bad-target-{Guid.NewGuid():N}.db")}");

        var error = await Assert.ThrowsAsync<BackupVerificationException>(() =>
            DurableStateTransferService.ImportAsync(artifact, targetFactory, true));
        Assert.Contains("intelligence", error.Message, StringComparison.OrdinalIgnoreCase);
        await using var target = await targetFactory.CreateDbContextAsync();
        Assert.Empty(await target.Tenants.ToListAsync());
        Assert.Empty(await target.IntelligencePolicies.ToListAsync());
        Assert.Empty(await target.RecommendationRuns.ToListAsync());
    }

    [Fact]
    public async Task Import_RejectsRecommendationScheduleThatCrossesItsPolicyScope()
    {
        Guid scheduleId;
        await using (var source = await _sourceFactory.CreateDbContextAsync())
        {
            var policy = await source.IntelligencePolicies.SingleAsync();
            scheduleId = Guid.CreateVersion7();
            var now = DateTimeOffset.UtcNow;
            source.JobSchedules.Add(new JobScheduleRecord
            {
                Id = scheduleId,
                TenantId = policy.TenantId,
                OwnerUserId = policy.OwnerUserId,
                LibraryScopeId = policy.LibraryScopeId,
                JobType = DurableScheduleEngine.RecommendationJobType,
                CronExpression = "0 8 * * *",
                TimeZoneId = "UTC",
                OverlapPolicy = ScheduleOverlapPolicy.Skip,
                MisfirePolicy = ScheduleMisfirePolicy.RunOnce,
                RetryPolicyJson = "{}",
                PayloadTemplateJson = JsonSerializer.Serialize(
                    new RecommendationScheduleTemplate(1, policy.Id, 25, "Transfer recommendations")),
                Enabled = true,
                NextRunAt = now.AddDays(1),
                CreatedAt = now,
                UpdatedAt = now,
                Revision = 0
            });
            await source.SaveChangesAsync();
        }
        var artifact = await _service.ExportAsync(Path.Combine(_root, "bad-recommendation-schedule"), true);
        var validTargetFactory = Factory($"postgres-fixture:{Path.Combine(_root, "valid-recommendation-schedule-target.db")}");
        await DurableStateTransferService.ImportAsync(artifact, validTargetFactory, true);
        await using (var validTarget = await validTargetFactory.CreateDbContextAsync())
        {
            var restored = await validTarget.JobSchedules.SingleAsync(item => item.Id == scheduleId);
            Assert.Equal(DurableScheduleEngine.RecommendationJobType, restored.JobType);
            Assert.Contains("Transfer recommendations", restored.PayloadTemplateJson, StringComparison.Ordinal);
        }
        artifact = await RewriteJsonArrayEntryAsync(artifact, "job-schedules.json", values =>
        {
            var schedule = values.Select(item => item!.AsObject()).Single(item =>
                Guid.Parse(item["id"]!.GetValue<string>()) == scheduleId);
            schedule["libraryScopeId"] = "another-library";
        });
        var targetFactory = Factory($"postgres-fixture:{Path.Combine(_root, "bad-recommendation-schedule-target.db")}");

        var error = await Assert.ThrowsAsync<BackupVerificationException>(() =>
            DurableStateTransferService.ImportAsync(artifact, targetFactory, true));

        Assert.Contains("intelligence", error.Message, StringComparison.OrdinalIgnoreCase);
        await using var target = await targetFactory.CreateDbContextAsync();
        Assert.Empty(await target.Tenants.ToListAsync());
        Assert.Empty(await target.JobSchedules.ToListAsync());
    }

    [Fact]
    public async Task ExportImport_PreservesSameExternalIdAcrossTenantsAndDisabledGlobalAccount()
    {
        var second = await AddTenantFixtureAsync("second");
        Guid firstTenantId;
        Guid firstCanonicalId;
        var globalAccountId = Guid.CreateVersion7();
        await using (var source = await _sourceFactory.CreateDbContextAsync())
        {
            firstTenantId = await source.Tenants
                .Where(item => item.Slug == "transfer")
                .Select(item => item.Id)
                .SingleAsync();
            firstCanonicalId = await source.CanonicalRecordings
                .Where(item => item.TenantId == firstTenantId)
                .Select(item => item.Id)
                .SingleAsync();
            source.ProviderAccounts.Add(new ProviderAccountRecord
            {
                Id = globalAccountId,
                ProviderId = "shared-fixture",
                DisplayName = "Disabled shared fixture",
                Scope = ProviderAccountScope.Global,
                Enabled = false,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            const string externalId = "shared-track-42";
            source.ProviderTrackIdentities.AddRange(
                CreateAccountIdentity(
                    firstTenantId,
                    firstCanonicalId,
                    globalAccountId,
                    "shared-fixture",
                    externalId),
                CreateAccountIdentity(
                    second.TenantId,
                    second.CanonicalRecordingId,
                    globalAccountId,
                    "shared-fixture",
                    externalId));
            await source.SaveChangesAsync();
        }

        var artifact = await _service.ExportAsync(
            Path.Combine(_root, "transfers"),
            writesQuiesced: true);
        var targetFactory = Factory(
            $"postgres-fixture:{Path.Combine(_root, "two-tenant-target.db")}");

        await DurableStateTransferService.ImportAsync(
            artifact,
            targetFactory,
            targetConfirmedEmpty: true);

        await using var target = await targetFactory.CreateDbContextAsync();
        var sharedIdentities = await target.ProviderTrackIdentities
            .Where(item => item.ProviderId == "shared-fixture")
            .ToListAsync();
        Assert.Equal(2, sharedIdentities.Count);
        Assert.Equal(2, sharedIdentities.Select(item => item.TenantId).Distinct().Count());
        Assert.All(sharedIdentities, item => Assert.Equal("shared-track-42", item.ExternalId));
        var restoredAccount = await target.ProviderAccounts.SingleAsync(
            item => item.Id == globalAccountId);
        Assert.Equal(ProviderAccountScope.Global, restoredAccount.Scope);
        Assert.False(restoredAccount.Enabled);
    }

    [Theory]
    [InlineData("../escaped.flac")]
    [InlineData("/another-root/escaped.flac")]
    [InlineData("/managed/music/../escaped.flac")]
    public async Task Import_RejectsUnsafeManagedFilePathBeforeDatabaseWrite(string unsafePath)
    {
        var artifact = await _service.ExportAsync(Path.Combine(_root, "transfers"), writesQuiesced: true);
        artifact = await RewriteJsonArrayEntryAsync(artifact, "managed-files.json",
            values => values[0]!["canonicalPath"] = unsafePath);

        var exception = await Assert.ThrowsAsync<BackupVerificationException>(() =>
            DurableStateTransferService.ImportAsync(artifact,
                Factory($"postgres-fixture:{Path.Combine(_root, $"unsafe-path-{Guid.NewGuid():N}.db")}"), true));

        Assert.Contains("managed file", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Import_RejectsManagedReferenceCountThatDoesNotMatchActiveReferences()
    {
        var artifact = await _service.ExportAsync(Path.Combine(_root, "transfers"), writesQuiesced: true);
        artifact = await RewriteJsonArrayEntryAsync(artifact, "managed-files.json",
            values => values[0]!["referenceCount"] = 2);

        var exception = await Assert.ThrowsAsync<BackupVerificationException>(() =>
            DurableStateTransferService.ImportAsync(artifact,
                Factory($"postgres-fixture:{Path.Combine(_root, "invalid-managed-reference-count.db")}"), true));

        Assert.Contains("reference count", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Import_RejectsManagedReferenceOutsideFileOwnershipScope()
    {
        var second = await AddTenantFixtureAsync("managed-reference-cross-scope");
        var artifact = await _service.ExportAsync(Path.Combine(_root, "transfers"), writesQuiesced: true);
        artifact = await RewriteJsonArrayEntryAsync(artifact, "managed-file-references.json", values =>
        {
            values[0]!["tenantId"] = second.TenantId;
            values[0]!["ownerUserId"] = second.UserId;
        });

        var exception = await Assert.ThrowsAsync<BackupVerificationException>(() =>
            DurableStateTransferService.ImportAsync(artifact,
                Factory($"postgres-fixture:{Path.Combine(_root, "cross-scope-managed-reference.db")}"), true));

        Assert.Contains("managed file reference", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Import_RejectsRouteCandidateUsingAnotherSameTenantUsersAccount()
    {
        var other = await AddSameTenantRouteUserAsync("candidate");
        var artifact = await _service.ExportAsync(Path.Combine(_root, "transfers"), writesQuiesced: true);
        artifact = await RewriteJsonArrayEntryAsync(artifact, "provider-route-decisions.json", values =>
        {
            var route = values[0]!.AsObject();
            var candidates = JsonNode.Parse(route["candidateDecisionsJson"]!.GetValue<string>())!.AsArray();
            candidates.Add(new JsonObject
            {
                ["providerId"] = "other-fixture",
                ["providerAccountId"] = other.AccountId,
                ["status"] = (int)ProviderRouteDecisionStatus.Rejected,
                ["reasonCode"] = "not-selected",
                ["priority"] = 1
            });
            route["candidateDecisionsJson"] = candidates.ToJsonString();
        });

        var exception = await Assert.ThrowsAsync<BackupVerificationException>(() =>
            DurableStateTransferService.ImportAsync(artifact,
                Factory($"postgres-fixture:{Path.Combine(_root, "cross-user-route-candidate.db")}"), true));

        Assert.Contains("provider route", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Import_RejectsRouteJobOwnedByAnotherSameTenantUser()
    {
        var other = await AddSameTenantRouteUserAsync("job");
        var artifact = await _service.ExportAsync(Path.Combine(_root, "transfers"), writesQuiesced: true);
        artifact = await RewriteJsonArrayEntryAsync(artifact, "provider-route-decisions.json",
            values => values[0]!["durableJobId"] = other.JobId);

        var exception = await Assert.ThrowsAsync<BackupVerificationException>(() =>
            DurableStateTransferService.ImportAsync(artifact,
                Factory($"postgres-fixture:{Path.Combine(_root, "cross-user-route-job.db")}"), true));

        Assert.Contains("provider route", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Export_IncludesProviderDownloadWorkspaceAndArtifactEntries()
    {
        var artifact = await _service.ExportAsync(Path.Combine(_root, "transfers"), writesQuiesced: true);
        using var archive = ZipFile.OpenRead(artifact.Path);
        Assert.True(archive.GetEntry("provider-download-workspaces.json")!.Length > 2);
        Assert.True(archive.GetEntry("provider-download-artifacts.json")!.Length > 2);
    }

    [Theory]
    [InlineData("../escape.flac")]
    [InlineData("/absolute.flac")]
    [InlineData("provider\\output.flac")]
    [InlineData("provider//output.flac")]
    public async Task Import_RejectsUnsafeProviderDownloadArtifactRelativePath(string unsafePath)
    {
        var artifact = await _service.ExportAsync(Path.Combine(_root, "transfers"), writesQuiesced: true);
        artifact = await RewriteJsonArrayEntryAsync(artifact, "provider-download-artifacts.json", values =>
        {
            values[0]!["relativePath"] = unsafePath;
            values[0]!["providerArtifactId"] = unsafePath;
        });

        var exception = await Assert.ThrowsAsync<BackupVerificationException>(() =>
            DurableStateTransferService.ImportAsync(artifact,
                Factory($"postgres-fixture:{Path.Combine(_root, $"unsafe-download-{Guid.NewGuid():N}.db")}"), true));

        Assert.Contains("download artifact", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Import_RejectsProviderDownloadWorkspaceCrossingJobScope()
    {
        var second = await AddTenantFixtureAsync("download-cross-scope");
        var artifact = await _service.ExportAsync(Path.Combine(_root, "transfers"), writesQuiesced: true);
        artifact = await RewriteJsonArrayEntryAsync(artifact, "provider-download-workspaces.json", values =>
        {
            values[0]!["tenantId"] = second.TenantId;
            values[0]!["ownerUserId"] = second.UserId;
        });

        var exception = await Assert.ThrowsAsync<BackupVerificationException>(() =>
            DurableStateTransferService.ImportAsync(artifact,
                Factory($"postgres-fixture:{Path.Combine(_root, "cross-download-workspace.db")}"), true));
        Assert.Contains("download workspace", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Import_RejectsProviderDownloadArtifactCrossingWorkspaceScope()
    {
        var second = await AddTenantFixtureAsync("download-artifact-cross-scope");
        var artifact = await _service.ExportAsync(Path.Combine(_root, "transfers"), writesQuiesced: true);
        artifact = await RewriteJsonArrayEntryAsync(artifact, "provider-download-artifacts.json", values =>
        {
            values[0]!["tenantId"] = second.TenantId;
            values[0]!["ownerUserId"] = second.UserId;
        });

        var exception = await Assert.ThrowsAsync<BackupVerificationException>(() =>
            DurableStateTransferService.ImportAsync(artifact,
                Factory($"postgres-fixture:{Path.Combine(_root, "cross-download-artifact.db")}"), true));
        Assert.Contains("download artifact", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Import_RejectsProviderDownloadArtifactWithInvalidHashLengthOrLifecycle()
    {
        var artifact = await _service.ExportAsync(Path.Combine(_root, "transfers"), writesQuiesced: true);
        artifact = await RewriteJsonArrayEntryAsync(artifact, "provider-download-artifacts.json", values =>
        {
            values[0]!["contentSha256"] = "BAD";
            values[0]!["length"] = 0;
            values[0]!["state"] = (int)ProviderDownloadArtifactState.Verified;
        });

        var exception = await Assert.ThrowsAsync<BackupVerificationException>(() =>
            DurableStateTransferService.ImportAsync(artifact,
                Factory($"postgres-fixture:{Path.Combine(_root, "invalid-download-facts.db")}"), true));
        Assert.Contains("download artifact", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Import_RejectsProviderDownloadArtifactLinkedToWrongManagedFileScope()
    {
        var second = await AddTenantFixtureAsync("download-managed-cross-scope");
        Guid foreignManagedFileId;
        await using (var source = await _sourceFactory.CreateDbContextAsync())
        {
            foreignManagedFileId = Guid.CreateVersion7();
            source.ManagedFiles.Add(new ManagedFileOwnershipEntity
            {
                Id = foreignManagedFileId,
                RootId = Guid.CreateVersion7(),
                TargetRootPath = "/managed/foreign",
                CanonicalPath = "/managed/foreign/file.flac",
                ContentSha256 = new string('e', 64),
                Length = 1,
                PlacementMethod = ManagedFilePlacementMethod.Copy,
                TenantId = second.TenantId,
                OwnerUserId = second.UserId,
                ScopeKey = "foreign",
                ReferenceCount = 1,
                IsManaged = true,
                CreatedAt = DateTimeOffset.UtcNow
            });
            source.ManagedFileReferences.Add(new ManagedFileReferenceEntity
            {
                Id = Guid.CreateVersion7(),
                ManagedFileId = foreignManagedFileId,
                TenantId = second.TenantId,
                OwnerUserId = second.UserId,
                ScopeKey = "foreign",
                ReferenceKey = "foreign:fixture",
                CreatedAt = DateTimeOffset.UtcNow,
                Revision = 1
            });
            await source.SaveChangesAsync();
        }
        var artifact = await _service.ExportAsync(Path.Combine(_root, "transfers"), writesQuiesced: true);
        artifact = await RewriteJsonArrayEntryAsync(artifact, "provider-download-artifacts.json",
            values => values[0]!["managedFileId"] = foreignManagedFileId);

        var exception = await Assert.ThrowsAsync<BackupVerificationException>(() =>
            DurableStateTransferService.ImportAsync(artifact,
                Factory($"postgres-fixture:{Path.Combine(_root, "cross-download-managed.db")}"), true));
        Assert.Contains("download artifact", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Import_RejectsRepeatedProviderDownloadArtifactIdentity()
    {
        var artifact = await _service.ExportAsync(Path.Combine(_root, "transfers"), writesQuiesced: true);
        artifact = await RewriteJsonArrayEntryAsync(artifact, "provider-download-artifacts.json", values =>
        {
            var duplicate = values[0]!.DeepClone();
            duplicate!["id"] = Guid.CreateVersion7();
            values.Add(duplicate);
        });

        var exception = await Assert.ThrowsAsync<BackupVerificationException>(() =>
            DurableStateTransferService.ImportAsync(artifact,
                Factory($"postgres-fixture:{Path.Combine(_root, "duplicate-download-artifact.db")}"), true));
        Assert.Contains("download artifact", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Import_RejectsFavoriteEventCrossTenantJobBeforeDatabaseWrite()
    {
        var second = await AddTenantFixtureAsync("phase6-favorite");
        var artifact = await _service.ExportAsync(Path.Combine(_root, "transfers"), writesQuiesced: true);
        artifact = await RewriteJsonArrayEntryAsync(artifact, "favorite-events.json", values =>
        {
            values[0]!["tenantId"] = second.TenantId;
            values[0]!["ownerUserId"] = second.UserId;
        });

        var exception = await Assert.ThrowsAsync<BackupVerificationException>(() =>
            DurableStateTransferService.ImportAsync(artifact,
                Factory($"postgres-fixture:{Path.Combine(_root, "cross-favorite-target.db")}"), true));

        Assert.Contains("favorite event", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Import_RejectsEnrichmentPlanCrossingManagedFileScopeBeforeDatabaseWrite()
    {
        var second = await AddTenantFixtureAsync("phase6-enrichment");
        var artifact = await _service.ExportAsync(Path.Combine(_root, "transfers"), writesQuiesced: true);
        artifact = await RewriteJsonArrayEntryAsync(artifact, "metadata-enrichment-plans.json", values =>
        {
            values[0]!["tenantId"] = second.TenantId;
            values[0]!["ownerUserId"] = second.UserId;
        });

        var exception = await Assert.ThrowsAsync<BackupVerificationException>(() =>
            DurableStateTransferService.ImportAsync(artifact,
                Factory($"postgres-fixture:{Path.Combine(_root, "cross-enrichment-target.db")}"), true));

        Assert.Contains("enrichment plan", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Import_RejectsMalformedEnrichmentDecisionJsonBeforeDatabaseWrite()
    {
        var artifact = await _service.ExportAsync(Path.Combine(_root, "transfers"), writesQuiesced: true);
        artifact = await RewriteJsonArrayEntryAsync(artifact, "metadata-enrichment-plans.json",
            values => values[0]!["decisionsJson"] = "{\"not\":\"an array\"}");

        var exception = await Assert.ThrowsAsync<BackupVerificationException>(() =>
            DurableStateTransferService.ImportAsync(artifact,
                Factory($"postgres-fixture:{Path.Combine(_root, "malformed-enrichment-target.db")}"), true));

        Assert.Contains("enrichment plan", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("global-owner")]
    [InlineData("global-null-action")]
    [InlineData("user-without-owner")]
    [InlineData("cross-tenant-actor")]
    [InlineData("invalid-protocol")]
    [InlineData("unsafe-library")]
    [InlineData("zero-revision")]
    [InlineData("duplicate-scope")]
    public async Task Import_RejectsMalformedFavoriteActionPolicyBeforeDatabaseWrite(string mutation)
    {
        var second = await AddTenantFixtureAsync($"policy-{mutation}");
        var artifact = await _service.ExportAsync(Path.Combine(_root, "transfers"), writesQuiesced: true);
        artifact = await RewriteJsonArrayEntryAsync(artifact, "favorite-action-policies.json", values =>
        {
            var global = values.Single(value => value!["scope"]!.GetValue<int>() == (int)FavoriteActionPolicyScope.Global)!;
            var user = values.Single(value => value!["scope"]!.GetValue<int>() == (int)FavoriteActionPolicyScope.User)!;
            switch (mutation)
            {
                case "global-owner": global["ownerUserId"] = second.UserId; break;
                case "global-null-action": global["autoDownload"] = null; break;
                case "user-without-owner": user["ownerUserId"] = null; break;
                case "cross-tenant-actor": global["updatedByUserId"] = second.UserId; break;
                case "invalid-protocol": global["protocol"] = "spotify"; break;
                case "unsafe-library": global["libraryScopeId"] = "music\nother"; break;
                case "zero-revision": global["revision"] = 0; break;
                case "duplicate-scope": values.Add(global.DeepClone()); break;
            }
        });

        var exception = await Assert.ThrowsAsync<BackupVerificationException>(() =>
            DurableStateTransferService.ImportAsync(artifact,
                Factory($"postgres-fixture:{Path.Combine(_root, $"policy-{mutation}-target.db")}"), true));

        Assert.Contains("favorite action policy", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Import_RejectsCanonicalCreatorFromAnotherTenantInTamperedArchive()
    {
        var second = await AddTenantFixtureAsync("cross-creator");
        var artifact = await _service.ExportAsync(
            Path.Combine(_root, "transfers"),
            writesQuiesced: true);
        artifact = await RewriteJsonArrayEntryAsync(
            artifact,
            "canonical-recordings.json",
            values => values[0]!["createdByUserId"] = second.UserId);

        var exception = await Assert.ThrowsAsync<BackupVerificationException>(() =>
            DurableStateTransferService.ImportAsync(
                artifact,
                Factory($"postgres-fixture:{Path.Combine(_root, "cross-creator-target.db")}"),
                targetConfirmedEmpty: true));

        Assert.Contains("canonical recording", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Import_RejectsCanonicalLinkFromAnotherTenantInTamperedArchive()
    {
        var second = await AddTenantFixtureAsync("cross-canonical");
        var artifact = await _service.ExportAsync(
            Path.Combine(_root, "transfers"),
            writesQuiesced: true);
        artifact = await RewriteJsonArrayEntryAsync(
            artifact,
            "provider-track-identities.json",
            values => values[0]!["tenantId"] = second.TenantId);

        var exception = await Assert.ThrowsAsync<BackupVerificationException>(() =>
            DurableStateTransferService.ImportAsync(
                artifact,
                Factory($"postgres-fixture:{Path.Combine(_root, "cross-canonical-target.db")}"),
                targetConfirmedEmpty: true));

        Assert.Contains("canonical recording boundary", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Import_RejectsAccountFromAnotherTenantInTamperedArchive()
    {
        var second = await AddTenantFixtureAsync("cross-account");
        var artifact = await _service.ExportAsync(
            Path.Combine(_root, "transfers"),
            writesQuiesced: true);
        artifact = await RewriteJsonArrayEntryAsync(
            artifact,
            "provider-accounts.json",
            values =>
            {
                values[0]!["tenantId"] = second.TenantId;
                values[0]!["ownerUserId"] = second.UserId;
            });

        var exception = await Assert.ThrowsAsync<BackupVerificationException>(() =>
            DurableStateTransferService.ImportAsync(
                artifact,
                Factory($"postgres-fixture:{Path.Combine(_root, "cross-account-target.db")}"),
                targetConfirmedEmpty: true));

        Assert.Contains("invalid tenant", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("malformed-provider")]
    [InlineData("invalid-scope-shape")]
    [InlineData("undefined-scope")]
    public async Task Import_RejectsMalformedProviderAccountInChecksumValidArchive(
        string mutation)
    {
        var artifact = await _service.ExportAsync(
            Path.Combine(_root, "transfers"),
            writesQuiesced: true);
        artifact = await RewriteJsonArrayEntryAsync(
            artifact,
            "provider-accounts.json",
            values => MutateProviderAccount(values[0]!.AsObject(), mutation));

        var exception = await Assert.ThrowsAsync<BackupVerificationException>(() =>
            DurableStateTransferService.ImportAsync(
                artifact,
                Factory($"postgres-fixture:{Path.Combine(_root, $"account-{mutation}-target.db")}"),
                targetConfirmedEmpty: true));

        Assert.Contains("provider account", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("catalog-with-account")]
    [InlineData("provider-mismatch")]
    [InlineData("uppercase-hash")]
    [InlineData("mismatched-hash")]
    [InlineData("unknown-kind")]
    [InlineData("non-track-kind")]
    [InlineData("unknown-scope")]
    [InlineData("unknown-verification")]
    [InlineData("zero-decision-version")]
    [InlineData("malformed-provider")]
    [InlineData("malformed-catalog")]
    [InlineData("malformed-external-id")]
    public async Task Import_RejectsMalformedTrackIdentityInChecksumValidArchive(string mutation)
    {
        var artifact = await _service.ExportAsync(
            Path.Combine(_root, "transfers"),
            writesQuiesced: true);
        artifact = await RewriteJsonArrayEntryAsync(
            artifact,
            "provider-track-identities.json",
            values => MutateTrackIdentity(values[0]!.AsObject(), mutation));

        var exception = await Assert.ThrowsAsync<BackupVerificationException>(() =>
            DurableStateTransferService.ImportAsync(
                artifact,
                Factory($"postgres-fixture:{Path.Combine(_root, $"{mutation}-target.db")}"),
                targetConfirmedEmpty: true));

        Assert.Contains("identity data is invalid", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("isrc", "usrc17607839")]
    [InlineData("musicBrainzRecordingId", "0D34FC3F-4F36-4B8D-A0D3-E0D7A8B6FF23")]
    public async Task Import_RejectsMalformedCanonicalSignalInChecksumValidArchive(
        string property,
        string value)
    {
        var artifact = await _service.ExportAsync(
            Path.Combine(_root, "transfers"),
            writesQuiesced: true);
        artifact = await RewriteJsonArrayEntryAsync(
            artifact,
            "canonical-recordings.json",
            values => values[0]![property] = value);

        var exception = await Assert.ThrowsAsync<BackupVerificationException>(() =>
            DurableStateTransferService.ImportAsync(
                artifact,
                Factory($"postgres-fixture:{Path.Combine(_root, $"{property}-target.db")}"),
                targetConfirmedEmpty: true));

        Assert.Contains("malformed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Export_RequiresCanonicalRecordingAndProviderTrackIdentityEntries()
    {
        var artifact = await _service.ExportAsync(
            Path.Combine(_root, "transfers"),
            writesQuiesced: true);

        using var archive = ZipFile.OpenRead(artifact.Path);
        var entryNames = archive.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("canonical-recordings.json", entryNames);
        Assert.Contains("provider-track-identities.json", entryNames);

        var canonicalEntry = Assert.Single(
            archive.Entries,
            entry => entry.FullName == "canonical-recordings.json");
        var identityEntry = Assert.Single(
            archive.Entries,
            entry => entry.FullName == "provider-track-identities.json");
        Assert.True(canonicalEntry.Length > 2);
        Assert.True(identityEntry.Length > 2);
    }

    [Fact]
    public async Task Export_RequiresExplicitWriteQuiescence()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.ExportAsync(Path.Combine(_root, "transfers"), writesQuiesced: false));

        Assert.Contains("quiescence", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Import_RejectsNonEmptyTarget()
    {
        var artifact = await _service.ExportAsync(
            Path.Combine(_root, "transfers"),
            writesQuiesced: true);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DurableStateTransferService.ImportAsync(
                artifact,
                _sourceFactory,
                targetConfirmedEmpty: true));

        Assert.Contains("not empty", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Import_RejectsTargetContainingOnlyOperationalState()
    {
        var artifact = await _service.ExportAsync(
            Path.Combine(_root, "transfers"),
            writesQuiesced: true);
        var targetFactory = Factory(
            $"postgres-fixture:{Path.Combine(_root, "operational-state-target.db")}");
        await using (var target = await targetFactory.CreateDbContextAsync())
        {
            await target.Database.MigrateAsync();
            target.Backups.Add(new BackupRecord
            {
                Id = Guid.CreateVersion7(),
                StorageProvider = "Postgres",
                ArtifactPath = "/fixture/backup.dump",
                Sha256 = new string('a', 64),
                SchemaVersion = _currentSchema,
                ApplicationVersion = AppVersion.Version,
                Status = "verified",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await target.SaveChangesAsync();
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DurableStateTransferService.ImportAsync(
                artifact,
                targetFactory,
                targetConfirmedEmpty: true));

        Assert.Contains("not empty", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Import_RejectsTargetContainingProviderDownloadWorkspaceState()
    {
        var artifact = await _service.ExportAsync(Path.Combine(_root, "transfers"), writesQuiesced: true);
        var targetFactory = Factory($"postgres-fixture:{Path.Combine(_root, "download-workspace-state-target.db")}");
        await using (var target = await targetFactory.CreateDbContextAsync())
        {
            await target.Database.MigrateAsync();
            var tenantId = Guid.CreateVersion7();
            var userId = Guid.CreateVersion7();
            var jobId = Guid.CreateVersion7();
            var now = DateTimeOffset.UtcNow;
            target.Tenants.Add(new TenantRecord
            {
                Id = tenantId,
                Slug = "target-download-state",
                Name = "Target download state",
                CreatedAt = now
            });
            target.Users.Add(new PlatformUserRecord
            {
                Id = userId,
                TenantId = tenantId,
                DisplayName = "Target user",
                Status = PlatformUserStatus.Active,
                CreatedAt = now,
                UpdatedAt = now
            });
            target.Jobs.Add(new DurableJobRecord
            {
                Id = jobId,
                ScopeKey = $"user:{tenantId:N}:{userId:N}",
                TenantId = tenantId,
                OwnerUserId = userId,
                PolicySnapshotJson = "{}",
                RequestFingerprint = new string('f', 64),
                CorrelationId = "target-download-state",
                Type = "fixture.download",
                PayloadJson = "{}",
                IdempotencyKey = "target-only",
                State = DurableJobState.Pending,
                MaxAttempts = 3,
                MaxDeferrals = 3,
                AvailableAt = now,
                CreatedAt = now,
                UpdatedAt = now
            });
            target.ProviderDownloadWorkspaces.Add(new ProviderDownloadWorkspaceEntity
            {
                Id = Guid.CreateVersion7(),
                WorkspaceId = new string('f', 64),
                TenantId = tenantId,
                OwnerUserId = userId,
                DurableJobId = jobId,
                ProviderId = "fixture",
                IdempotencyKey = "target-only",
                CreatedAt = now,
                Revision = 1
            });
            await target.SaveChangesAsync();
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DurableStateTransferService.ImportAsync(artifact, targetFactory, targetConfirmedEmpty: true));
        Assert.Contains("not empty", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("canonical_recordings")]
    [InlineData("provider_track_identities")]
    public async Task Import_RejectsTargetContainingOnlyPhase2IdentityState(string table)
    {
        var artifact = await _service.ExportAsync(
            Path.Combine(_root, "transfers"),
            writesQuiesced: true);
        var targetFactory = Factory(
            $"postgres-fixture:{Path.Combine(_root, $"{table}-target.db")}");
        await using (var target = await targetFactory.CreateDbContextAsync())
        {
            await target.Database.MigrateAsync();
            var tenantId = Guid.CreateVersion7();
            var userId = Guid.CreateVersion7();
            var canonicalRecordingId = Guid.CreateVersion7();
            var now = DateTimeOffset.UtcNow;
            target.Tenants.Add(new TenantRecord
            {
                Id = tenantId,
                Slug = $"target-{tenantId:N}",
                Name = "Target fixture",
                CreatedAt = now
            });
            target.Users.Add(new PlatformUserRecord
            {
                Id = userId,
                TenantId = tenantId,
                DisplayName = "Target fixture",
                Status = PlatformUserStatus.Active,
                CreatedAt = now,
                UpdatedAt = now
            });
            target.CanonicalRecordings.Add(new CanonicalRecordingRecord
            {
                Id = canonicalRecordingId,
                TenantId = tenantId,
                CreatedByUserId = userId,
                CreatedAt = now,
                UpdatedAt = now,
                Revision = 1
            });
            if (table == "provider_track_identities")
            {
                target.ProviderTrackIdentities.Add(new ProviderTrackIdentityRecord
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    CanonicalRecordingId = canonicalRecordingId,
                    ProviderId = "fixture",
                    ResourceKind = ProviderResourceKind.Track,
                    CatalogNamespace = "default",
                    Scope = ProviderIdentityScope.Catalog,
                    ExternalId = "fixture-track",
                    ExternalIdHash = HashExternalId("fixture-track"),
                    Verification = ProviderIdentityVerification.Verified,
                    VerificationMethod = "fixture",
                    DecisionVersion = 1,
                    VerifiedAt = now,
                    CreatedAt = now,
                    UpdatedAt = now,
                    Revision = 1
                });
            }
            await target.SaveChangesAsync();
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DurableStateTransferService.ImportAsync(
                artifact,
                targetFactory,
                targetConfirmedEmpty: true));

        Assert.Contains("not empty", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Import_RejectsSchemaFromAnotherBuildBeforeMigratingTarget()
    {
        var artifact = await _service.ExportAsync(
            Path.Combine(_root, "transfers"),
            writesQuiesced: true);
        const string futureSchema = "99991231235959_FutureAllstarrSchema";
        artifact = await RewriteManifestAsync(
            artifact,
            json => json.Replace(
                $"\"schemaVersion\":\"{_currentSchema}\"",
                $"\"schemaVersion\":\"{futureSchema}\"",
                StringComparison.Ordinal));
        artifact = artifact with { SchemaVersion = futureSchema };
        var targetPath = Path.Combine(_root, "incompatible-schema-target.db");

        var exception = await Assert.ThrowsAsync<BackupVerificationException>(() =>
            DurableStateTransferService.ImportAsync(
                artifact,
                Factory($"postgres-fixture:{targetPath}"),
                targetConfirmedEmpty: true));

        Assert.Contains("schema", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Import_RejectsArtifactFromAnotherApplicationVersion()
    {
        var artifact = await _service.ExportAsync(
            Path.Combine(_root, "transfers"),
            writesQuiesced: true);
        artifact = await RewriteManifestAsync(
            artifact,
            json => json.Replace(
                $"\"applicationVersion\":\"{AppVersion.Version}\"",
                "\"applicationVersion\":\"999.0.0\"",
                StringComparison.Ordinal));
        var targetPath = Path.Combine(_root, "incompatible-application-target.db");

        var exception = await Assert.ThrowsAsync<BackupVerificationException>(() =>
            DurableStateTransferService.ImportAsync(
                artifact,
                Factory($"postgres-fixture:{targetPath}"),
                targetConfirmedEmpty: true));

        Assert.Contains("application", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadArtifact_RejectsUnknownManifestFieldsAndProviderValues()
    {
        var artifact = await _service.ExportAsync(
            Path.Combine(_root, "transfers"),
            writesQuiesced: true);
        var unknownFieldArtifact = await RewriteManifestAsync(
            artifact,
            json => json.Replace(
                "\"secretKeyMaterialIncluded\":false",
                "\"secretKeyMaterialIncluded\":false,\"unexpected\":\"value\"",
                StringComparison.Ordinal));

        var unknownFieldException = await Assert.ThrowsAsync<BackupVerificationException>(() =>
            DurableStateTransferService.LoadArtifactAsync(
                unknownFieldArtifact.Path,
                unknownFieldArtifact.Sha256));
        Assert.Contains("unknown", unknownFieldException.Message, StringComparison.OrdinalIgnoreCase);

        var secondArtifact = await _service.ExportAsync(
            Path.Combine(_root, "transfers"),
            writesQuiesced: true);
        var unknownProviderArtifact = await RewriteManifestAsync(
            secondArtifact,
            json => json.Replace(
                "\"sourceProvider\":\"Postgres\"",
                "\"sourceProvider\":\"Automatic\"",
                StringComparison.Ordinal));

        var providerException = await Assert.ThrowsAsync<BackupVerificationException>(() =>
            DurableStateTransferService.LoadArtifactAsync(
                unknownProviderArtifact.Path,
                unknownProviderArtifact.Sha256));
        Assert.Contains("provider", providerException.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadArtifact_RejectsUnknownArchiveEntries()
    {
        var artifact = await _service.ExportAsync(
            Path.Combine(_root, "transfers"),
            writesQuiesced: true);
        using (var archive = ZipFile.Open(artifact.Path, ZipArchiveMode.Update))
        {
            var entry = archive.CreateEntry("secret-key-material.json");
            await using var stream = entry.Open();
            await stream.WriteAsync("not-allowed"u8.ToArray());
        }

        artifact = artifact with { Sha256 = await ComputeSha256Async(artifact.Path) };

        var exception = await Assert.ThrowsAsync<BackupVerificationException>(() =>
            DurableStateTransferService.LoadArtifactAsync(artifact.Path, artifact.Sha256));

        Assert.Contains("unknown", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("canonical-recordings.json")]
    [InlineData("provider-track-identities.json")]
    [InlineData("onboarding-states.json")]
    public async Task LoadArtifact_RejectsMissingTrackIdentityArchiveEntries(string entryName)
    {
        var artifact = await _service.ExportAsync(
            Path.Combine(_root, "transfers"),
            writesQuiesced: true);
        using (var archive = ZipFile.Open(artifact.Path, ZipArchiveMode.Update))
        {
            (archive.GetEntry(entryName)
             ?? throw new InvalidOperationException($"Fixture entry '{entryName}' is missing."))
                .Delete();
        }

        artifact = artifact with { Sha256 = await ComputeSha256Async(artifact.Path) };

        var exception = await Assert.ThrowsAsync<BackupVerificationException>(() =>
            DurableStateTransferService.LoadArtifactAsync(artifact.Path, artifact.Sha256));

        Assert.Contains("missing", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Import_RejectsRequestedMetadataThatDoesNotMatchManifest()
    {
        var artifact = await _service.ExportAsync(
            Path.Combine(_root, "transfers"),
            writesQuiesced: true);

        var exception = await Assert.ThrowsAsync<BackupVerificationException>(() =>
            DurableStateTransferService.ImportAsync(
                artifact with { SourceProvider = "Other" },
                Factory($"postgres-fixture:{Path.Combine(_root, "metadata-target.db")}"),
                targetConfirmedEmpty: true));

        Assert.Contains("metadata", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<(Guid TenantId, Guid UserId, Guid CanonicalRecordingId)>
        AddTenantFixtureAsync(string suffix)
    {
        var tenantId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var canonicalRecordingId = Guid.CreateVersion7();
        await using var source = await _sourceFactory.CreateDbContextAsync();
        source.Tenants.Add(new TenantRecord
        {
            Id = tenantId,
            Slug = $"transfer-{suffix}",
            Name = $"Transfer {suffix}",
            CreatedAt = DateTimeOffset.UtcNow
        });
        source.Users.Add(new PlatformUserRecord
        {
            Id = userId,
            TenantId = tenantId,
            DisplayName = $"Transfer {suffix} user",
            Status = PlatformUserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        source.CanonicalRecordings.Add(new CanonicalRecordingRecord
        {
            Id = canonicalRecordingId,
            TenantId = tenantId,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Revision = 1
        });
        await source.SaveChangesAsync();
        return (tenantId, userId, canonicalRecordingId);
    }

    private async Task<(Guid UserId, Guid AccountId, Guid JobId)> AddSameTenantRouteUserAsync(string suffix)
    {
        await using var source = await _sourceFactory.CreateDbContextAsync();
        var route = await source.ProviderRouteDecisions.AsNoTracking().SingleAsync();
        var userId = Guid.CreateVersion7();
        var accountId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        source.Users.Add(new PlatformUserRecord
        {
            Id = userId,
            TenantId = route.TenantId,
            DisplayName = $"Other route {suffix} user",
            Status = PlatformUserStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        });
        source.ProviderAccounts.Add(new ProviderAccountRecord
        {
            Id = accountId,
            TenantId = route.TenantId,
            OwnerUserId = userId,
            ProviderId = "other-fixture",
            DisplayName = $"Other route {suffix} account",
            Scope = ProviderAccountScope.User,
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now
        });
        source.Jobs.Add(new DurableJobRecord
        {
            Id = jobId,
            TenantId = route.TenantId,
            OwnerUserId = userId,
            ScopeKey = $"user:{route.TenantId:N}:{userId:N}",
            Type = "route.transfer.test",
            PayloadJson = "{}",
            IdempotencyKey = $"route-transfer-{suffix}",
            State = DurableJobState.Succeeded,
            MaxAttempts = 3,
            MaxDeferrals = 3,
            AvailableAt = now,
            PolicySnapshotJson = "{}",
            RequestFingerprint = new string('9', 64),
            CorrelationId = $"route-transfer-{suffix}",
            CreatedAt = now,
            UpdatedAt = now,
            CompletedAt = now
        });
        await source.SaveChangesAsync();
        return (userId, accountId, jobId);
    }

    private static ProviderTrackIdentityRecord CreateAccountIdentity(
        Guid tenantId,
        Guid canonicalRecordingId,
        Guid providerAccountId,
        string providerId,
        string externalId) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            CanonicalRecordingId = canonicalRecordingId,
            ProviderAccountId = providerAccountId,
            ProviderId = providerId,
            ResourceKind = ProviderResourceKind.Track,
            CatalogNamespace = "default",
            Scope = ProviderIdentityScope.Account,
            ExternalId = externalId,
            ExternalIdHash = HashExternalId(externalId),
            Verification = ProviderIdentityVerification.Verified,
            VerificationMethod = "fixture",
            DecisionVersion = 1,
            VerifiedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Revision = 1
        };

    private static void MutateTrackIdentity(JsonObject identity, string mutation)
    {
        switch (mutation)
        {
            case "catalog-with-account":
                identity["scope"] = (int)ProviderIdentityScope.Catalog;
                break;
            case "provider-mismatch":
                identity["providerId"] = "other-provider";
                break;
            case "uppercase-hash":
                identity["externalIdHash"] = identity["externalIdHash"]!
                    .GetValue<string>()
                    .ToUpperInvariant();
                break;
            case "mismatched-hash":
                identity["externalIdHash"] = new string('a', 64);
                break;
            case "unknown-kind":
                identity["resourceKind"] = (int)ProviderResourceKind.Unknown;
                break;
            case "non-track-kind":
                identity["resourceKind"] = (int)ProviderResourceKind.Album;
                break;
            case "unknown-scope":
                identity["scope"] = (int)ProviderIdentityScope.Unknown;
                break;
            case "unknown-verification":
                identity["verification"] = (int)ProviderIdentityVerification.Unknown;
                break;
            case "zero-decision-version":
                identity["decisionVersion"] = 0;
                break;
            case "malformed-provider":
                identity["providerId"] = "Fixture";
                break;
            case "malformed-catalog":
                identity["catalogNamespace"] = " Default ";
                break;
            case "malformed-external-id":
                identity["externalId"] = " fixture-track-42 ";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }
    }

    private static void MutateProviderAccount(JsonObject account, string mutation)
    {
        switch (mutation)
        {
            case "malformed-provider":
                account["providerId"] = "Fixture";
                break;
            case "invalid-scope-shape":
                account["ownerUserId"] = null;
                break;
            case "undefined-scope":
                account["scope"] = 99;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }
    }

    private static async Task<DurableStateTransferArtifact> RewriteJsonArrayEntryAsync(
        DurableStateTransferArtifact artifact,
        string entryName,
        Action<JsonArray> transform)
    {
        string entryJson;
        using (var archive = ZipFile.Open(artifact.Path, ZipArchiveMode.Update))
        {
            var entry = archive.GetEntry(entryName)
                        ?? throw new InvalidOperationException(
                            $"Fixture entry '{entryName}' is missing.");
            await using (var stream = entry.Open())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                entryJson = await reader.ReadToEndAsync();
            }

            entry.Delete();
            var values = JsonNode.Parse(entryJson)?.AsArray()
                         ?? throw new InvalidOperationException(
                             $"Fixture entry '{entryName}' is not a JSON array.");
            transform(values);
            var replacement = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            await using var replacementStream = replacement.Open();
            await using var writer = new StreamWriter(
                replacementStream,
                new UTF8Encoding(false));
            await writer.WriteAsync(values.ToJsonString());
        }

        return artifact with { Sha256 = await ComputeSha256Async(artifact.Path) };
    }

    private static async Task<DurableStateTransferArtifact> RewriteManifestAsync(
        DurableStateTransferArtifact artifact,
        Func<string, string> transform)
    {
        string manifestJson;
        using (var archive = ZipFile.Open(artifact.Path, ZipArchiveMode.Update))
        {
            var manifest = archive.GetEntry("manifest.json")
                           ?? throw new InvalidOperationException("Fixture manifest is missing.");
            await using (var stream = manifest.Open())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                manifestJson = await reader.ReadToEndAsync();
            }

            manifest.Delete();
            var replacement = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
            await using var replacementStream = replacement.Open();
            await using var writer = new StreamWriter(replacementStream, new UTF8Encoding(false));
            await writer.WriteAsync(transform(manifestJson));
        }

        return artifact with { Sha256 = await ComputeSha256Async(artifact.Path) };
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private static string HashExternalId(string externalId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(externalId)))
            .ToLowerInvariant();

    private TestDbContextFactory Factory(string databaseKey)
    {
        if (!_databases.TryGetValue(databaseKey, out var database))
        {
            database = PostgresTestDatabase.CreateAsync().GetAwaiter().GetResult();
            _databases[databaseKey] = database;
        }

        return new TestDbContextFactory(database.Options);
    }

    public async Task DisposeAsync()
    {
        foreach (var database in _databases.Values.Distinct())
        {
            await database.DisposeAsync();
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);

        public Task<AllstarrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(new AllstarrDbContext(options));
    }
}
