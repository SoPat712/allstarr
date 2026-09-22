using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Storage;
using allstarr.Services.MusicBrainz;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class CanonicalCatalogStorageTests : IAsyncLifetime
{
    private PostgresTestDatabase _database = null!;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateAsync();

    [Fact]
    public async Task CanonicalGraph_PersistsRecordingCreditsEditionTrackAliasAndProvenance()
    {
        var now = new DateTimeOffset(2026, 9, 14, 20, 0, 0, TimeSpan.Zero);
        var tenantId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var artistId = Guid.CreateVersion7();
        var recordingId = Guid.CreateVersion7();
        var releaseGroupId = Guid.CreateVersion7();
        var releaseId = Guid.CreateVersion7();
        var releaseTrackId = Guid.CreateVersion7();

        await using (var db = new AllstarrDbContext(_database.Options))
        {
            db.Tenants.Add(new TenantRecord
            {
                Id = tenantId,
                Slug = "catalog-graph",
                Name = "Catalog graph",
                CreatedAt = now
            });
            db.Users.Add(new PlatformUserRecord
            {
                Id = userId,
                TenantId = tenantId,
                DisplayName = "Catalog owner",
                Status = PlatformUserStatus.Active,
                CreatedAt = now,
                UpdatedAt = now
            });
            db.CanonicalArtists.Add(new CanonicalArtistRecord
            {
                Id = artistId,
                TenantId = tenantId,
                Name = "Example Artist",
                SortName = "Example Artist",
                MusicBrainzArtistId = "11111111-1111-1111-1111-111111111111",
                CreatedAt = now,
                UpdatedAt = now
            });
            db.CanonicalRecordings.Add(new CanonicalRecordingRecord
            {
                Id = recordingId,
                TenantId = tenantId,
                CreatedByUserId = userId,
                Title = "Example Recording",
                DurationMilliseconds = 183_000,
                MusicBrainzRecordingId = "22222222-2222-2222-2222-222222222222",
                CreatedAt = now,
                UpdatedAt = now
            });
            db.CanonicalReleaseGroups.Add(new CanonicalReleaseGroupRecord
            {
                Id = releaseGroupId,
                TenantId = tenantId,
                Title = "Example Album",
                PrimaryType = "Album",
                MusicBrainzReleaseGroupId = "33333333-3333-3333-3333-333333333333",
                CreatedAt = now,
                UpdatedAt = now
            });
            db.CanonicalReleases.Add(new CanonicalReleaseRecord
            {
                Id = releaseId,
                TenantId = tenantId,
                CanonicalReleaseGroupId = releaseGroupId,
                Title = "Example Album",
                CountryCode = "US",
                ReleaseDate = "2026-09-14",
                MusicBrainzReleaseId = "44444444-4444-4444-4444-444444444444",
                CreatedAt = now,
                UpdatedAt = now
            });
            db.CanonicalReleaseTracks.Add(new CanonicalReleaseTrackRecord
            {
                Id = releaseTrackId,
                TenantId = tenantId,
                CanonicalReleaseId = releaseId,
                CanonicalRecordingId = recordingId,
                MediumPosition = 1,
                TrackPosition = 1,
                Title = "Example Recording",
                DurationMilliseconds = 183_000,
                MusicBrainzTrackId = "55555555-5555-5555-5555-555555555555",
                CreatedAt = now,
                UpdatedAt = now
            });
            db.CanonicalRecordingArtists.Add(new CanonicalRecordingArtistRecord
            {
                TenantId = tenantId,
                CanonicalRecordingId = recordingId,
                CanonicalArtistId = artistId,
                Position = 0
            });
            db.CanonicalReleaseGroupArtists.Add(new CanonicalReleaseGroupArtistRecord
            {
                TenantId = tenantId,
                CanonicalReleaseGroupId = releaseGroupId,
                CanonicalArtistId = artistId,
                Position = 0
            });
            db.CanonicalCatalogAliases.Add(new CanonicalCatalogAliasRecord
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                EntityKind = CanonicalCatalogEntityKind.Recording,
                CanonicalEntityId = recordingId,
                Namespace = "legacy-protocol-id",
                ExternalId = "ext-spotify-example",
                ExternalIdHash = new string('a', 64),
                CreatedAt = now,
                LastSeenAt = now
            });
            db.CatalogFacts.Add(new CatalogFactRecord
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                EntityKind = CanonicalCatalogEntityKind.Recording,
                CanonicalEntityId = recordingId,
                FieldName = "title",
                ValueJson = "\"Example Recording\"",
                SourceId = "fixture",
                SourceRevision = "fixture-1",
                PayloadSha256 = new string('b', 64),
                Confidence = 1,
                ObservedAt = now,
                RefreshAfter = now.AddDays(7)
            });

            await db.SaveChangesAsync();
        }

        await using var verification = new AllstarrDbContext(_database.Options);
        var track = await verification.CanonicalReleaseTracks.SingleAsync();
        Assert.Equal(recordingId, track.CanonicalRecordingId);
        Assert.Equal(releaseId, track.CanonicalReleaseId);
        Assert.Equal(artistId, (await verification.CanonicalRecordingArtists.SingleAsync()).CanonicalArtistId);
        Assert.Equal(recordingId, (await verification.CanonicalCatalogAliases.SingleAsync()).CanonicalEntityId);
        var fact = await verification.CatalogFacts.SingleAsync();
        Assert.Equal("fixture", fact.SourceId);

        fact.SourceId = "rewritten-source";
        await Assert.ThrowsAsync<InvalidOperationException>(() => verification.SaveChangesAsync());
    }

    [Fact]
    public async Task EvidenceStore_IsIdempotentSupersedesFactsAndRejectsAliasRemapping()
    {
        var now = new DateTimeOffset(2026, 9, 14, 21, 0, 0, TimeSpan.Zero);
        var tenantId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var recordingId = Guid.CreateVersion7();
        var otherRecordingId = Guid.CreateVersion7();
        await using (var db = new AllstarrDbContext(_database.Options))
        {
            db.Tenants.Add(new TenantRecord
            {
                Id = tenantId,
                Slug = "catalog-evidence",
                Name = "Catalog evidence",
                CreatedAt = now
            });
            db.Users.Add(new PlatformUserRecord
            {
                Id = userId,
                TenantId = tenantId,
                DisplayName = "Catalog owner",
                Status = PlatformUserStatus.Active,
                CreatedAt = now,
                UpdatedAt = now
            });
            db.CanonicalRecordings.AddRange(
                Recording(recordingId, tenantId, userId, "First", now),
                Recording(otherRecordingId, tenantId, userId, "Second", now));
            await db.SaveChangesAsync();
        }

        var storage = new DurableStorageOptions
        {
            Provider = "Postgres",
            ConnectionString = _database.ConnectionString
        };
        var storageState = new DurableStorageState(storage);
        storageState.Set(DurableStorageReadiness.Ready, "fixture");
        var store = new CanonicalCatalogEvidenceStore(
            new TestDbContextFactory(_database.Options),
            storageState);
        var actor = new ProviderActorContext(
            tenantId,
            ProviderActorKind.User,
            userId,
            new ProviderBackendPrincipal("jellyfin", "fixture", "catalog-user"));
        var target = new CanonicalCatalogEntityReference(
            CanonicalCatalogEntityKind.Recording,
            recordingId);
        var alias = new CanonicalCatalogAliasInput("Legacy-Protocol-ID", "ext-spotify-track");
        var initial = new CanonicalCatalogSourceStamp(
            "Fixture",
            "revision-1",
            new string('a', 64),
            0.9,
            now,
            now.AddDays(7));

        var first = await store.RecordAsync(
            actor,
            target,
            initial,
            [alias],
            [new CanonicalCatalogFactInput("title", "  \"First\"  ")]);
        var repeated = await store.RecordAsync(
            actor,
            target,
            initial,
            [alias],
            [new CanonicalCatalogFactInput("title", "\"First\"")]);
        var changed = await store.RecordAsync(
            actor,
            target,
            initial with
            {
                SourceRevision = "revision-2",
                PayloadSha256 = new string('b', 64),
                ObservedAt = now.AddDays(1),
                RefreshAfter = now.AddDays(8)
            },
            [alias],
            [new CanonicalCatalogFactInput("title", "\"First (Remastered)\"")]);

        Assert.Equal(new CanonicalCatalogEvidenceResult(1, 0, 1, 0), first);
        Assert.Equal(new CanonicalCatalogEvidenceResult(0, 1, 0, 0), repeated);
        Assert.Equal(new CanonicalCatalogEvidenceResult(0, 1, 1, 1), changed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RecordAsync(
            actor,
            target with { Id = otherRecordingId },
            initial,
            [alias],
            []));

        await using var verification = new AllstarrDbContext(_database.Options);
        Assert.Single(await verification.CanonicalCatalogAliases.ToListAsync());
        var facts = await verification.CatalogFacts.OrderBy(item => item.ObservedAt).ToListAsync();
        Assert.Equal(2, facts.Count);
        Assert.NotNull(facts[0].SupersededAt);
        Assert.Null(facts[1].SupersededAt);
        Assert.Equal("fixture", facts[1].SourceId);
    }

    [Fact]
    public async Task BrainzMashGraphIngest_IsAtomicIdempotentAndPreservesEditionIdentity()
    {
        var now = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
        var tenantId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        await SeedActorAsync(tenantId, userId, "brainzmash-ingest", now);
        var service = new MusicBrainzCatalogIngestService(
            new TestDbContextFactory(_database.Options),
            ReadyStorage());
        var actor = Actor(tenantId, userId);
        var artist = new MusicBrainzArtist
        {
            Id = "11111111-1111-4111-8111-111111111111",
            Name = "Nicky Youre",
            SortName = "Youre, Nicky"
        };
        var credit = new MusicBrainzArtistCredit { Name = artist.Name, Artist = artist };
        var releaseGroup = new MusicBrainzReleaseGroup
        {
            Id = "22222222-2222-4222-8222-222222222222",
            Title = "Sunroof",
            PrimaryType = "Single",
            FirstReleaseDate = "2021-12-03",
            ArtistCredit = [credit]
        };
        var release = new MusicBrainzRelease
        {
            Id = "33333333-3333-4333-8333-333333333333",
            Title = "Sunroof",
            Date = "2021-12-03",
            Country = "US",
            Status = "Official",
            ReleaseGroup = releaseGroup,
            ArtistCredit = [credit],
            Media =
            [
                new MusicBrainzMedium
                {
                    Position = 1,
                    TrackCount = 2,
                    Tracks =
                    [
                        Track(
                            "44444444-4444-4444-8444-444444444444",
                            "55555555-5555-4555-8555-555555555555",
                            1,
                            "Sunroof",
                            163_000,
                            credit),
                        Track(
                            "66666666-6666-4666-8666-666666666666",
                            "77777777-7777-4777-8777-777777777777",
                            2,
                            "Sunroof (Acoustic)",
                            135_000,
                            credit)
                    ]
                }
            ]
        };
        var graph = new MusicBrainzCatalogGraph(release, releaseGroup, [artist]);
        var source = new MusicBrainzCatalogSource(
            "BrainzMash", "brainzmash:ws2", now, now.AddDays(7));

        var first = await service.IngestAsync(actor, graph, source);
        var repeated = await service.IngestAsync(
            actor,
            graph,
            source with { ObservedAt = now.AddDays(1), RefreshAfter = now.AddDays(8) });

        Assert.Equal(first.ReleaseGroupId, repeated.ReleaseGroupId);
        Assert.Equal(first.ReleaseId, repeated.ReleaseId);
        Assert.Equal(7, first.EntitiesCreated);
        Assert.Equal(0, repeated.EntitiesCreated);
        Assert.Equal(7, first.Evidence.AliasesCreated);
        Assert.Equal(7, first.Evidence.FactsCreated);
        Assert.Equal(7, repeated.Evidence.AliasesSeen);
        Assert.Equal(0, repeated.Evidence.FactsCreated);
        Assert.Equal(0, repeated.Evidence.FactsSuperseded);

        await using (var verification = new AllstarrDbContext(_database.Options))
        {
            Assert.Single(await verification.CanonicalArtists.Where(item => item.TenantId == tenantId).ToListAsync());
            Assert.Single(await verification.CanonicalReleaseGroups.Where(item => item.TenantId == tenantId).ToListAsync());
            Assert.Single(await verification.CanonicalReleases.Where(item => item.TenantId == tenantId).ToListAsync());
            Assert.Equal(2, await verification.CanonicalRecordings.CountAsync(item => item.TenantId == tenantId));
            Assert.Equal(2, await verification.CanonicalReleaseTracks.CountAsync(item => item.TenantId == tenantId));
            Assert.Equal(7, await verification.CanonicalCatalogAliases.CountAsync(item => item.TenantId == tenantId));
            Assert.Equal(7, await verification.CatalogFacts.CountAsync(item => item.TenantId == tenantId));
            Assert.All(
                await verification.CanonicalRecordings.Where(item => item.TenantId == tenantId).ToListAsync(),
                item => Assert.False(item.IsProvisional));
        }

        var invalidTenantId = Guid.CreateVersion7();
        var invalidUserId = Guid.CreateVersion7();
        await SeedActorAsync(invalidTenantId, invalidUserId, "brainzmash-invalid", now);
        release.Media![0].Tracks![1].Position = 1;
        await Assert.ThrowsAsync<ArgumentException>(() => service.IngestAsync(
            Actor(invalidTenantId, invalidUserId), graph, source));
        await using var atomicVerification = new AllstarrDbContext(_database.Options);
        Assert.Equal(0, await atomicVerification.CanonicalReleaseGroups.CountAsync(
            item => item.TenantId == invalidTenantId));
        Assert.Equal(0, await atomicVerification.CanonicalRecordings.CountAsync(
            item => item.TenantId == invalidTenantId));
        Assert.Equal(0, await atomicVerification.CatalogFacts.CountAsync(
            item => item.TenantId == invalidTenantId));
    }

    private static MusicBrainzReleaseTrack Track(
        string trackId,
        string recordingId,
        int position,
        string title,
        int duration,
        MusicBrainzArtistCredit credit) => new()
        {
            Id = trackId,
            Position = position,
            Title = title,
            Length = duration,
            ArtistCredit = [credit],
            Recording = new MusicBrainzRecording
            {
                Id = recordingId,
                Title = title,
                Length = duration,
                ArtistCredit = [credit]
            }
        };

    private async Task SeedActorAsync(
        Guid tenantId,
        Guid userId,
        string slug,
        DateTimeOffset now)
    {
        await using var db = new AllstarrDbContext(_database.Options);
        db.Tenants.Add(new TenantRecord
        {
            Id = tenantId,
            Slug = slug,
            Name = slug,
            CreatedAt = now
        });
        db.Users.Add(new PlatformUserRecord
        {
            Id = userId,
            TenantId = tenantId,
            DisplayName = "Catalog owner",
            Status = PlatformUserStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
    }

    private static ProviderActorContext Actor(Guid tenantId, Guid userId) => new(
        tenantId,
        ProviderActorKind.User,
        userId,
        new ProviderBackendPrincipal("jellyfin", "fixture", "catalog-user"));

    private DurableStorageState ReadyStorage()
    {
        var state = new DurableStorageState(new DurableStorageOptions
        {
            Provider = "Postgres",
            ConnectionString = _database.ConnectionString
        });
        state.Set(DurableStorageReadiness.Ready, "fixture");
        return state;
    }

    private static CanonicalRecordingRecord Recording(
        Guid id,
        Guid tenantId,
        Guid userId,
        string title,
        DateTimeOffset now) => new()
        {
            Id = id,
            TenantId = tenantId,
            CreatedByUserId = userId,
            Title = title,
            IsProvisional = true,
            CreatedAt = now,
            UpdatedAt = now
        };

    private sealed class TestDbContextFactory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);

        public Task<AllstarrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();
}
