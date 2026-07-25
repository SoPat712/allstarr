using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Enrichment;
using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Playlists.Targets;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace allstarr.Tests;

public sealed class MetadataEnrichmentTests
{
    [Fact]
    public void Plan_PreservesLocalValuesAndDeterministicallyAddsMusicBrainzAndProviderValues()
    {
        var planner = new MetadataEnrichmentPlanner();
        var local = new LocalMetadataSnapshot(new("My title", true), new("My artist", true), Genre: new(null));
        var mb = new MusicBrainzEnrichmentSnapshot(
            "31E68C1D-31F9-432C-A3A4-13AEF4A53833", null, null, null,
            "Remote title", "Remote artist", "Album", "Album artist", ["Rock", "rock", "Indie"], 2024, 2);
        var providers = new[] { new ProviderMetadataSnapshot("qobuz", "rev-1",
            new Dictionary<string, string?> { ["album"] = "Provider album", ["year"] = "2023" }) };

        var first = planner.CreatePlan(local, mb, providers);
        var second = planner.CreatePlan(local, mb, providers);

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal("My title", first.Tags["title"]);
        Assert.Equal("Album", first.Tags["album"]);
        Assert.Equal("Indie; Rock", first.Tags["genre"]);
        Assert.Equal("31e68c1d-31f9-432c-a3a4-13aef4a53833", first.Tags["musicbrainz_recordingid"]);
        Assert.Contains(first.Decisions, decision => decision.Field == "title" && decision.Reason == "local_user_edit_preserved");
        Assert.True(first.ManagedArtifactsOnly);
    }

    [Fact]
    public void Plan_RejectsMalformedMusicBrainzIdentity()
    {
        var planner = new MetadataEnrichmentPlanner();
        Assert.Throws<ArgumentException>(() => planner.CreatePlan(
            new(new("Title"), new("Artist")),
            new("not-an-id", null, null, null, null, null, null, null, null, null, null)));
    }

    [Fact]
    public void Plan_RejectsUnboundedOrDuplicateProviderSnapshots()
    {
        var planner = new MetadataEnrichmentPlanner();
        var local = new LocalMetadataSnapshot(new("Title"), new("Artist"));
        Assert.Throws<ArgumentException>(() => planner.CreatePlan(local, null,
            [new("bad/provider", "revision", new Dictionary<string, string?>())]));
        Assert.Throws<ArgumentException>(() => planner.CreatePlan(local, null,
            [new("qobuz", "one", new Dictionary<string, string?>()), new("QOBUZ", "two", new Dictionary<string, string?>())]));
        Assert.Throws<ArgumentException>(() => planner.CreatePlan(local, null,
            [new("qobuz", new string('r', 201), new Dictionary<string, string?>())]));
    }

    [Fact]
    public async Task Applicator_WritesOnlyManagedNonSourceArtifact()
    {
        var writer = new RecordingWriter();
        var service = new ManagedMetadataPlanApplicator(writer);
        var plan = new MetadataEnrichmentPlanner().CreatePlan(new(new("Title"), new("Artist")), null);
        var sha = new string('a', 64);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(new("/library/source.flac", sha, false, true), plan));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(new("/managed/link.flac", sha, true, true), plan));
        var result = await service.ApplyAsync(new("/managed/copy.flac", sha, true, false)
        {
            TargetRootPath = "/managed",
            OperationFingerprint = plan.Fingerprint
        }, plan);

        Assert.Equal("/managed/copy.flac", Assert.Single(writer.Artifacts).Path);
        Assert.Equal(sha, result.ContentSha256);
    }

    [Fact]
    public async Task ManagedWriter_RecoversWhenApplicationTransitionFailsAfterOwnershipUpdate()
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-enrichment-writer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "track.flac");
            await File.WriteAllTextAsync(path, "original");
            var original = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path)));
            var mutator = new AppendingMutator();
            var operations = new allstarr.Core.ManagedFiles.PhysicalManagedFileOperations();
            Assert.True(operations.TryGetFileIdentity(path, out var originalIdentity));
            var writer = new TagLibManagedMetadataWriter(mutator, operations);
            var artifact = new ManagedMetadataArtifact(path, original, true, false)
            {
                TargetRootPath = root,
                FileSystemDeviceId = originalIdentity.DeviceId,
                FileSystemFileId = originalIdentity.FileId,
                OperationFingerprint = new string('b', 64)
            };

            var written = await writer.WriteAsync(artifact, new Dictionary<string, string> { ["title"] = "Tagged" }, default);

            Assert.False(written.Reused);
            Assert.NotEqual(original, written.ContentSha256);
            Assert.NotEqual(originalIdentity.FileId, written.FileSystemFileId);
            Assert.Equal("original\ntagged", await File.ReadAllTextAsync(path));
            Assert.NotEqual(path, mutator.LastAppliedPath);
            Assert.NotNull(written.Lease);
            await written.Lease!.DisposeAsync(); // Simulate a crash after the swap but before DB commit.

            var retryArtifact = artifact with
            {
                FileSystemDeviceId = written.FileSystemDeviceId,
                FileSystemFileId = written.FileSystemFileId
            };
            var recovered = await writer.WriteAsync(retryArtifact,
                new Dictionary<string, string> { ["title"] = "Tagged" }, default);
            Assert.True(recovered.Reused);
            Assert.Equal(written.ContentSha256, recovered.ContentSha256);
            Assert.Equal(1, mutator.ApplyCount);
            await recovered.Lease!.CommitAsync(default);
            Assert.DoesNotContain(Directory.EnumerateFiles(root), item => item != path);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ManagedWriter_RejectsAncestorSymlinkSwapWithoutTouchingOutsideFile()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "allstarr-enrichment-symlink", Guid.NewGuid().ToString("N"));
        var managed = Path.Combine(root, "managed");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(managed);
        Directory.CreateDirectory(outside);
        try
        {
            var outsideFile = Path.Combine(outside, "track.flac");
            await File.WriteAllTextAsync(outsideFile, "outside-safe");
            Directory.CreateSymbolicLink(Path.Combine(managed, "Artist"), outside);
            var path = Path.Combine(managed, "Artist", "track.flac");
            var hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(outsideFile)));
            var writer = new TagLibManagedMetadataWriter(new AppendingMutator(),
                new allstarr.Core.ManagedFiles.PhysicalManagedFileOperations());

            await Assert.ThrowsAsync<IOException>(() => writer.WriteAsync(new(path, hash, true, false)
            {
                TargetRootPath = managed,
                OperationFingerprint = new string('c', 64)
            }, new Dictionary<string, string> { ["title"] = "Tagged" }, default));

            Assert.Equal("outside-safe", await File.ReadAllTextAsync(outsideFile));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ManagedWriter_DoesNotTreatExternalSameTagEditAsCompletedSwap()
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-enrichment-external", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "track.flac");
            await File.WriteAllTextAsync(path, "original");
            var original = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path)));
            await File.AppendAllTextAsync(path, "\ntagged");
            var writer = new TagLibManagedMetadataWriter(new AppendingMutator(),
                new allstarr.Core.ManagedFiles.PhysicalManagedFileOperations());

            await Assert.ThrowsAsync<IOException>(() => writer.WriteAsync(new(path, original, true, false)
            {
                TargetRootPath = root,
                OperationFingerprint = new string('d', 64)
            }, new Dictionary<string, string> { ["title"] = "Tagged" }, default));

            Assert.Equal("original\ntagged", await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ManagedWriter_DifferentOperationCannotAdoptOrOverwritePendingSwap()
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-enrichment-conflict", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "track.flac");
            await File.WriteAllTextAsync(path, "original");
            var original = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path)));
            var operations = new allstarr.Core.ManagedFiles.PhysicalManagedFileOperations();
            Assert.True(operations.TryGetFileIdentity(path, out var identity));
            var writer = new TagLibManagedMetadataWriter(new AppendingMutator(), operations);
            var first = await writer.WriteAsync(new(path, original, true, false)
            {
                TargetRootPath = root,
                FileSystemDeviceId = identity.DeviceId,
                FileSystemFileId = identity.FileId,
                OperationFingerprint = new string('e', 64)
            }, new Dictionary<string, string> { ["title"] = "First" }, default);
            await first.Lease!.DisposeAsync();

            await Assert.ThrowsAsync<IOException>(() => writer.WriteAsync(new(path, original, true, false)
            {
                TargetRootPath = root,
                FileSystemDeviceId = identity.DeviceId,
                FileSystemFileId = identity.FileId,
                OperationFingerprint = new string('f', 64)
            }, new Dictionary<string, string> { ["title"] = "Second" }, default));

            Assert.Equal("original\ntagged", await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DurableApplications_DoNotReuseAnAppliedRecordForAChangedArtifactChecksum()
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-enrichment-applications", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var database = await PostgresTestDatabase.CreateAsync();
            var factory = new DbFactory(database.Options);
            var tenantId = Guid.CreateVersion7();
            var userId = Guid.CreateVersion7();
            var jobId = Guid.CreateVersion7();
            var fileId = Guid.CreateVersion7();
            var planId = Guid.CreateVersion7();
            var now = DateTimeOffset.UtcNow;
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.MigrateAsync();
                db.Tenants.Add(new TenantRecord { Id = tenantId, Slug = "enrichment-apps", Name = "Enrichment apps", CreatedAt = now });
                db.Users.Add(new PlatformUserRecord
                {
                    Id = userId,
                    TenantId = tenantId,
                    DisplayName = "Owner",
                    Status = PlatformUserStatus.Active,
                    CreatedAt = now,
                    UpdatedAt = now
                });
                db.Jobs.Add(new DurableJobRecord
                {
                    Id = jobId,
                    TenantId = tenantId,
                    OwnerUserId = userId,
                    ScopeKey = $"user:{tenantId:N}:{userId:N}",
                    Type = "enrichment.test",
                    PayloadJson = "{}",
                    IdempotencyKey = "enrichment-test",
                    State = DurableJobState.Running,
                    MaxAttempts = 3,
                    MaxDeferrals = 3,
                    AvailableAt = now,
                    PolicySnapshotJson = "{}",
                    RequestFingerprint = new string('1', 64),
                    CorrelationId = "enrichment-test",
                    CreatedAt = now,
                    UpdatedAt = now
                });
                db.ManagedFiles.Add(new allstarr.Core.ManagedFiles.ManagedFileOwnershipEntity
                {
                    Id = fileId,
                    RootId = Guid.CreateVersion7(),
                    TargetRootPath = root,
                    CanonicalPath = Path.Combine(root, "track.flac"),
                    ContentSha256 = new string('a', 64),
                    Length = 1,
                    PlacementMethod = allstarr.Core.ManagedFiles.ManagedFilePlacementMethod.Copy,
                    TenantId = tenantId,
                    OwnerUserId = userId,
                    SourceJobId = jobId,
                    ScopeKey = $"user:{tenantId:N}:{userId:N}",
                    ReferenceCount = 0,
                    IsManaged = true,
                    CreatedAt = now
                });
                db.MetadataEnrichmentPlans.Add(new MetadataEnrichmentPlanRecord
                {
                    Id = planId,
                    TenantId = tenantId,
                    OwnerUserId = userId,
                    LineageJobId = jobId,
                    ManagedArtifactId = fileId,
                    Fingerprint = new string('b', 64),
                    PlanVersion = 1,
                    SourceRevisionsJson = "[]",
                    DecisionsJson = "[]",
                    TagsJson = "{}",
                    PathValuesJson = "{}",
                    CreatedAt = now
                });
                await db.SaveChangesAsync();
            }
            var service = new DurableMetadataEnrichmentService(factory, new Clock());
            var first = await service.BeginApplicationAsync(new(
                tenantId, userId, jobId, fileId, planId, new string('a', 64)));
            await service.MarkAppliedAsync(tenantId, userId, first.Id);
            var changed = await service.BeginApplicationAsync(new(
                tenantId, userId, jobId, fileId, planId, new string('c', 64)));
            var recoveredPending = await service.BeginApplicationAsync(new(
                tenantId, userId, jobId, fileId, planId, new string('d', 64)));

            Assert.NotEqual(first.Id, changed.Id);
            Assert.Equal(MetadataEnrichmentApplicationState.Pending, changed.State);
            Assert.Equal(changed.Id, recoveredPending.Id);
            Assert.Equal(new string('c', 64), recoveredPending.ArtifactContentSha256);
            await using var verify = await factory.CreateDbContextAsync();
            Assert.Equal(2, await verify.MetadataEnrichmentApplications.CountAsync());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void TagMutator_WritesPicardCompatibleMusicBrainzFields()
    {
        var tag = new Mock<TagLib.Tag>();
        tag.SetupAllProperties();
        var values = new Dictionary<string, string>
        {
            ["musicbrainz_recordingid"] = "31e68c1d-31f9-432c-a3a4-13aef4a53833",
            ["musicbrainz_releaseid"] = "41e68c1d-31f9-432c-a3a4-13aef4a53833",
            ["musicbrainz_releasegroupid"] = "51e68c1d-31f9-432c-a3a4-13aef4a53833",
            ["musicbrainz_artistid"] = "61e68c1d-31f9-432c-a3a4-13aef4a53833"
        };

        TagLibManagedTagFileMutator.ApplyTags(tag.Object, values);

        Assert.Equal(values["musicbrainz_recordingid"], tag.Object.MusicBrainzTrackId);
        Assert.Equal(values["musicbrainz_releaseid"], tag.Object.MusicBrainzReleaseId);
        Assert.Equal(values["musicbrainz_releasegroupid"], tag.Object.MusicBrainzReleaseGroupId);
        Assert.Equal(values["musicbrainz_artistid"], tag.Object.MusicBrainzArtistId);
    }

    [Fact]
    public async Task JellyfinRefresh_PostsScanWithoutMediaMutation()
    {
        var handler = new RecordingHandler("{}");
        var refresher = new JellyfinLibraryRefresher(new HttpClient(handler),
            new JellyfinSettings { Url = "https://jellyfin.test", ApiKey = "secret" });
        var result = await refresher.RefreshAsync(Context(ProtocolKind.Jellyfin), new("music"), default);
        Assert.True(result.Accepted);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("/Library/Refresh", handler.Request.RequestUri!.AbsolutePath);
        Assert.Null(handler.Request.Content);
    }

    [Fact]
    public async Task SubsonicRefresh_UsesEphemeralCredentialAndSurfacesBackendFailure()
    {
        var successHandler = new RecordingHandler("""{"subsonic-response":{"status":"ok","scanStatus":{"count":3}}}""");
        var refresher = new SubsonicLibraryRefresher(new HttpClient(successHandler),
            new SubsonicSettings { Url = "https://navidrome.test" }, new AuthenticationResolver());
        var result = await refresher.RefreshAsync(Context(ProtocolKind.Subsonic), new("music", Guid.CreateVersion7()), default);
        Assert.Equal("3", result.NativeScanId);
        Assert.DoesNotContain("password", successHandler.Request!.RequestUri!.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("p=password", successHandler.Body, StringComparison.Ordinal);

        var failure = new SubsonicLibraryRefresher(new HttpClient(new RecordingHandler(
            """{"subsonic-response":{"status":"failed"}}""")), new SubsonicSettings { Url = "https://navidrome.test" }, new AuthenticationResolver());
        await Assert.ThrowsAsync<HttpRequestException>(() => failure.RefreshAsync(Context(ProtocolKind.Subsonic), new("music", Guid.CreateVersion7()), default));
    }

    [Fact]
    public void Resolver_UsesExactProtocolAndRejectsMissingAdapter()
    {
        var fake = new FakeRefresher();
        var resolver = new BackendLibraryRefresherResolver([fake]);
        Assert.Same(fake, resolver.Resolve(ProtocolKind.Jellyfin));
        Assert.Throws<NotSupportedException>(() => resolver.Resolve(ProtocolKind.Subsonic));
    }

    [Fact]
    public async Task RefreshJob_RejectsCrossTenantIdentityWithoutCallingBackend()
    {
        var root = Path.Combine(Path.GetTempPath(), "allstarr-refresh-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var database = await PostgresTestDatabase.CreateAsync();
            var factory = new DbFactory(database.Options);
            var tenant = Guid.CreateVersion7(); var foreignTenant = Guid.CreateVersion7(); var user = Guid.CreateVersion7();
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.MigrateAsync();
                db.Tenants.AddRange(
                    new TenantRecord { Id = tenant, Slug = "owner", Name = "Owner", CreatedAt = DateTimeOffset.UtcNow },
                    new TenantRecord { Id = foreignTenant, Slug = "foreign", Name = "Foreign", CreatedAt = DateTimeOffset.UtcNow });
                db.Users.Add(new PlatformUserRecord
                {
                    Id = user,
                    TenantId = foreignTenant,
                    DisplayName = "Foreign",
                    Status = PlatformUserStatus.Active,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
                db.BackendIdentities.Add(new BackendIdentityRecord
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = foreignTenant,
                    UserId = user,
                    BackendType = "jellyfin",
                    BackendInstanceId = "backend",
                    PrincipalId = "principal",
                    CreatedAt = DateTimeOffset.UtcNow,
                    LastSeenAt = DateTimeOffset.UtcNow
                });
                await db.SaveChangesAsync();
            }
            var fake = new FakeRefresher();
            var handler = new BackendLibraryRefreshJobHandler(factory, new([fake]), new Clock());
            var payload = JsonSerializer.SerializeToElement(new BackendLibraryRefreshJobPayload("music", "backend", "principal"));
            var claim = new DurableJobClaim(Guid.CreateVersion7(), Guid.CreateVersion7(), 1, "library.refresh", payload,
                tenant, user, null, "music", null, JsonSerializer.SerializeToElement(new { }), "correlation", "worker", DateTimeOffset.UtcNow.AddMinutes(1));

            var result = await handler.ExecuteAsync(new(claim,
                Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(
                    new Microsoft.Extensions.DependencyInjection.ServiceCollection())), default);

            Assert.Equal(DurableJobCompletionKind.Failed, result.Kind);
            Assert.Equal("library_refresh_identity_unavailable", result.ErrorCode);
            Assert.Equal(0, fake.Calls);
        }
        finally { Directory.Delete(root, true); }
    }

    private static ProtocolExecutionContext Context(ProtocolKind protocol) => new(protocol, "primary", "principal",
        new AllstarrPrincipal(Guid.CreateVersion7(), Guid.CreateVersion7(), protocol == ProtocolKind.Jellyfin ? "jellyfin" : "subsonic",
            "primary", "principal", "Owner", false), "refresh-test", DateTimeOffset.UtcNow.AddMinutes(5), default, libraryScopeId: "music");

    private sealed class RecordingWriter : IManagedMetadataWriter
    {
        public List<ManagedMetadataArtifact> Artifacts { get; } = [];
        public Task<ManagedMetadataWriteResult> WriteAsync(ManagedMetadataArtifact artifact,
            IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken)
        {
            Artifacts.Add(artifact);
            return Task.FromResult(new ManagedMetadataWriteResult(artifact.ContentSha256, 1, Reused: false));
        }
    }
    private sealed class AppendingMutator : IManagedTagFileMutator
    {
        public int ApplyCount { get; private set; }
        public string? LastAppliedPath { get; private set; }

        public void Apply(string path, IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyCount++;
            LastAppliedPath = path;
            File.AppendAllText(path, "\ntagged");
        }

        public bool Matches(string path, IReadOnlyDictionary<string, string> tags) =>
            File.ReadAllText(path).EndsWith("\ntagged", StringComparison.Ordinal);
    }
    private sealed class AuthenticationResolver : IBackendPlaylistAuthenticationResolver
    {
        public ValueTask<BackendPlaylistAuthentication> ResolveAsync(BackendPlaylistTargetContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new BackendPlaylistAuthentication(new Dictionary<string, string>(), [new("u", "user"), new("p", "password")]));
    }
    private sealed class RecordingHandler(string response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string Body { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
    private sealed class FakeRefresher : IBackendLibraryRefresher
    {
        public int Calls { get; private set; }
        public ProtocolKind Protocol => ProtocolKind.Jellyfin;
        public Task<BackendLibraryRefreshResult> RefreshAsync(ProtocolExecutionContext context, BackendLibraryRefreshRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(Count());
        private BackendLibraryRefreshResult Count() { Calls++; return new(true); }
    }
    private sealed class Clock : IPlatformClock { public DateTimeOffset UtcNow => new(2026, 7, 12, 8, 0, 0, TimeSpan.Zero); }
    private sealed class DbFactory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
