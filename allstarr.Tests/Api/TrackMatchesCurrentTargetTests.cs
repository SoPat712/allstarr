using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Admin;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed class TrackMatchesCurrentTargetTests : IAsyncLifetime
{
    private readonly Guid owner = Guid.NewGuid();
    private readonly Mock<ITrackMatchRepository> repository = new(MockBehavior.Strict);
    private readonly Mock<IProtocolProviderGateway> gateway = new(MockBehavior.Strict);
    private readonly Mock<IApplicationCache> cache = new();
    private SqliteTestDatabase database = null!;
    private Factory factory = null!;
    private ExternalMetadataSnapshotRecord source = null!;
    private ManualTrackOverrideRecord authority = null!;
    private TrackMatchReviewData review = null!;

    public async Task InitializeAsync()
    {
        database = await SqliteTestDatabase.CreateAsync();
        factory = new(database.Options);
        await using var db = factory.CreateDbContext();
        db.Users.Add(new()
        {
            Id = owner,
            DisplayName = "Listener",
            Enabled = true,
            BackendType = "jellyfin",
            BackendInstanceId = "fixture",
            BackendPrincipalId = "listener"
        });
        await db.SaveChangesAsync();
        source = new()
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            ProviderId = "spotify",
            ResourceKind = "track",
            Protocol = "jellyfin",
            BackendInstanceId = "fixture",
            BackendPrincipalId = "listener",
            ExternalIdHash = new string('a', 64),
            SnapshotVersion = 1,
            PayloadSha256 = new string('c', 64),
            ProviderRevision = "fixture",
            CorrelationId = "fixture",
            RetrievedAt = DateTimeOffset.UtcNow,
            PayloadJson = """{"title":"Source title","artist":"Source artist","album":"Source album","durationMilliseconds":999000,"isrc":"SOURCE"}"""
        };
        authority = new()
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            ExternalSnapshotId = source.Id,
            SourceProviderId = source.ProviderId,
            SourceExternalIdHash = source.ExternalIdHash,
            Decision = ManualOverrideDecision.Pin,
            TargetProviderId = "deezer",
            TargetExternalId = "current"
        };
        review = new([source], [], [authority], [], []);
        repository.Setup(item => item.GetReviewDataAsync(new TrackMatchActor(owner, false), null, null, source.Id, 5000,
                It.IsAny<CancellationToken>())).ReturnsAsync(() => review);
        gateway.Setup(item => item.GetPlayableProviderOrderAsync(It.IsAny<ProviderActorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["deezer"]);
    }

    public Task DisposeAsync() => database.DisposeAsync().AsTask();

    [Fact]
    public async Task CurrentFetchesTheResolvedRecordingFactsInsteadOfTheSourceFacts()
    {
        gateway.Setup(item => item.GetExactSongAsync(It.Is<ProtocolExecutionContext>(context => context.IsReadOnly), "deezer", "current"))
            .ReturnsAsync(new Song
            {
                ExternalProvider = "deezer",
                ExternalId = "current",
                Title = "Actual title",
                Artist = "Actual artist",
                Album = "Actual album",
                Duration = 241,
                PreciseDuration = TimeSpan.FromMilliseconds(241160),
                Isrc = "ACTUAL"
            });

        var target = (await Read()).GetProperty("target");

        Assert.Equal("Actual title", target.GetProperty("title").GetString());
        Assert.Equal("Actual artist", target.GetProperty("artist").GetString());
        Assert.Equal("Actual album", target.GetProperty("album").GetString());
        Assert.Equal(241160, target.GetProperty("durationMilliseconds").GetInt64());
        Assert.Equal("ACTUAL", target.GetProperty("isrc").GetString());
        Assert.Equal("deezer", target.GetProperty("providerId").GetString());
        Assert.Equal("current", target.GetProperty("externalId").GetString());
        Assert.False(target.GetProperty("isLocal").GetBoolean());
        gateway.Verify(item => item.GetExactSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "current"), Times.Once);
        repository.VerifyAll();
    }

    [Fact]
    public async Task MissingTargetFactsDoNotBorrowSourceOrCanonicalMetadata()
    {
        var recording = Guid.NewGuid();
        var sourceIdentity = Identity(recording, "spotify", "source", source.ExternalIdHash);
        var targetIdentity = Identity(recording, "deezer", "current", new string('b', 64));
        source.ProviderTrackIdentityId = sourceIdentity.Id;
        await using (var db = factory.CreateDbContext())
        {
            db.CanonicalRecordings.Add(new()
            {
                Id = recording,
                CreatedByUserId = owner,
                Title = "Source canonical title",
                DurationMilliseconds = 999000,
                PublicId = recording.ToString("N")
            });
            db.ProviderTrackIdentities.AddRange(sourceIdentity, targetIdentity);
            db.ExternalMetadataSnapshots.Add(source);
            await db.SaveChangesAsync();
        }
        review = review with { ProviderIdentities = [sourceIdentity, targetIdentity] };
        gateway.Setup(item => item.GetExactSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "current"))
            .ReturnsAsync((Song?)null);

        var target = (await Read()).GetProperty("target");

        foreach (var field in new[] { "title", "artist", "album", "durationMilliseconds", "isrc" })
            Assert.Equal(JsonValueKind.Null, target.GetProperty(field).ValueKind);
    }

    [Fact]
    public async Task ProviderFailurePreservesOnlyTheCurrentTargetsCachedFacts()
    {
        cache.Setup(item => item.GetStringAsync(CacheKeyBuilder.BuildPlaybackMetadataKey("deezer", "current")))
            .ReturnsAsync("""{"metadata":{"title":"Cached target","album":"Cached release","durationMilliseconds":240500,"isrc":"CACHED"}}""");
        gateway.Setup(item => item.GetExactSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "current"))
            .ThrowsAsync(new HttpRequestException("Metadata unavailable"));

        var target = (await Read()).GetProperty("target");

        Assert.Equal("Cached target", target.GetProperty("title").GetString());
        Assert.Equal("Cached release", target.GetProperty("album").GetString());
        Assert.Equal(240500, target.GetProperty("durationMilliseconds").GetInt64());
        Assert.Equal("CACHED", target.GetProperty("isrc").GetString());
        Assert.Equal(JsonValueKind.Null, target.GetProperty("artist").ValueKind);
    }

    [Fact]
    public async Task DifferentProviderRecordingCannotFillTheCurrentTargetsFacts()
    {
        gateway.Setup(item => item.GetExactSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "current"))
            .ReturnsAsync(new Song { ExternalProvider = "deezer", ExternalId = "different", Title = "Wrong recording", Duration = 9 });

        var target = (await Read()).GetProperty("target");

        Assert.Equal(JsonValueKind.Null, target.GetProperty("title").ValueKind);
        Assert.Equal(JsonValueKind.Null, target.GetProperty("durationMilliseconds").ValueKind);
    }

    [Fact]
    public async Task LocalCurrentMatchUsesTheIndexedRecordingWithoutAProviderRead()
    {
        var track = new LibraryTrackRecord
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            Protocol = "jellyfin",
            BackendInstanceId = "fixture",
            BackendLibraryId = "music",
            BackendItemId = "local-track",
            Title = "Local title",
            Artist = "Local artist",
            Album = "Local album",
            DurationMilliseconds = 240123,
            Isrc = "LOCAL"
        };
        authority.TargetProviderId = null; authority.TargetExternalId = null; authority.LibraryTrackId = track.Id;
        review = review with { LibraryTracks = [track] };

        var target = (await Read()).GetProperty("target");

        Assert.True(target.GetProperty("isLocal").GetBoolean());
        Assert.Equal(track.Id, target.GetProperty("libraryTrackId").GetGuid());
        Assert.Equal("local-track", target.GetProperty("backendItemId").GetString());
        Assert.Equal("Local title", target.GetProperty("title").GetString());
        Assert.Equal("Local artist", target.GetProperty("artist").GetString());
        Assert.Equal("Local album", target.GetProperty("album").GetString());
        Assert.Equal(240123, target.GetProperty("durationMilliseconds").GetInt64());
        Assert.Equal("LOCAL", target.GetProperty("isrc").GetString());
        gateway.Verify(item => item.GetExactSongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ForeignSourceCannotTriggerATargetMetadataRead()
    {
        source.OwnerUserId = Guid.NewGuid();

        Assert.IsType<NotFoundResult>(await Controller().Current(source.Id));

        gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UnresolvedSourceReturnsNoInventedCurrentRecording()
    {
        review = review with { ActiveOverrides = [] };

        Assert.Equal(JsonValueKind.Null, (await Read()).GetProperty("target").ValueKind);

        gateway.Verify(item => item.GetExactSongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    private async Task<JsonElement> Read()
    {
        var controller = Controller();
        var result = Assert.IsType<OkObjectResult>(await controller.Current(source.Id));
        Assert.Equal("private, no-store", controller.Response.Headers.CacheControl.ToString());
        return JsonSerializer.SerializeToElement(result.Value);
    }

    private TrackMatchesController Controller()
    {
        var http = new DefaultHttpContext();
        http.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "fixture",
            UserId = "listener",
            UserName = "Listener",
            IsAdministrator = false,
            BackendType = "jellyfin",
            BackendInstanceId = "fixture",
            AllstarrUserId = owner,
            JellyfinAccessToken = "fixture",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        return new TrackMatchesController(repository.Object, gateway.Object, new(factory, new SystemPlatformClock()),
            factory, null!, null!, new TrackMatchDecisionEngine(), null!, applicationCache: cache.Object)
        { ControllerContext = new() { HttpContext = http } };
    }

    private static ProviderTrackIdentityRecord Identity(Guid recording, string provider, string id, string hash) => new()
    {
        Id = Guid.NewGuid(),
        CanonicalRecordingId = recording,
        ProviderId = provider,
        ExternalId = id,
        ExternalIdHash = hash,
        ResourceKind = ProviderResourceKind.Track,
        Scope = ProviderIdentityScope.Catalog,
        Verification = ProviderIdentityVerification.Verified,
        DecisionVersion = 1,
        VerificationMethod = "fixture"
    };

    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
