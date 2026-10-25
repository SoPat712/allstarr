using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed class TrackMatchesProviderSearchTests : IAsyncLifetime
{
    private readonly Guid _user = Guid.CreateVersion7();
    private readonly Guid _snapshot = Guid.CreateVersion7();
    private readonly Mock<IProtocolProviderGateway> _gateway = new(MockBehavior.Strict);
    private SqliteTestDatabase _database = null!;
    private Factory _factory = null!;

    public async Task InitializeAsync()
    {
        _database = await SqliteTestDatabase.CreateAsync();
        _factory = new(_database.Options);
        await using var db = _factory.CreateDbContext();
        db.Users.Add(new UserRecord
        {
            Id = _user,
            BackendType = "jellyfin",
            BackendInstanceId = "fixture",
            BackendPrincipalId = "listener",
            DisplayName = "Listener",
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        db.ExternalMetadataSnapshots.Add(new ExternalMetadataSnapshotRecord
        {
            Id = _snapshot,
            OwnerUserId = _user,
            ProviderId = "spotify",
            ResourceKind = "track",
            BackendInstanceId = "fixture",
            BackendPrincipalId = "listener",
            Protocol = "jellyfin",
            ExternalIdHash = new string('a', 64),
            SnapshotVersion = 1,
            ProviderRevision = "1",
            PayloadSha256 = new string('b', 64),
            CorrelationId = "fixture",
            PayloadJson = """{"title":"Communicate","artist":"who do we blame","durationMilliseconds":115000}""",
            RetrievedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        _gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["artistgrid"]);
        _gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Download)).Returns([]);
    }

    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

    [Fact]
    public async Task SearchProjectsBestVersionFlagAndScoreEvidence()
    {
        var songs = Versions();
        songs[2].IsBestVersion = true;
        _gateway.Setup(item => item.SearchPlayableSongsAsync(It.IsAny<ProtocolExecutionContext>(), "Communicate", 20))
            .ReturnsAsync(songs);
        var tracks = await SearchAsync("Communicate");
        var best = Assert.Single(tracks, item => item.GetProperty("isBestVersion").GetBoolean());
        Assert.Equal("v3", best.GetProperty("externalId").GetString());
        Assert.Contains(best.GetProperty("reasons").EnumerateArray(), item => item.GetString() == "ArtistGrid best version +5");
    }

    [Theory]
    [InlineData("Communicate who do we blame")]
    [InlineData("who do we blame Communicate")]
    [InlineData("  COMMUNICATE   who do we blame  ")]
    public async Task EmptySourceTitleAndArtistQueryRetriesTheTitleOnce(string query)
    {
        _gateway.Setup(item => item.SearchPlayableSongsAsync(It.IsAny<ProtocolExecutionContext>(), query.Trim(), 20))
            .ReturnsAsync([]);
        _gateway.Setup(item => item.SearchPlayableSongsAsync(It.IsAny<ProtocolExecutionContext>(), "Communicate", 20))
            .ReturnsAsync(Versions());

        var tracks = await SearchAsync(query);

        AssertVersions(tracks);
        _gateway.Verify(item => item.SearchPlayableSongsAsync(It.IsAny<ProtocolExecutionContext>(), query.Trim(), 20), Times.Once);
        _gateway.Verify(item => item.SearchPlayableSongsAsync(It.IsAny<ProtocolExecutionContext>(), "Communicate", 20), Times.Once);
        _gateway.Verify(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming), Times.Once);
        _gateway.Verify(item => item.GetProviderOrder(ProviderCapabilityKind.Download), Times.Once);
        _gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TitleSearchKeepsEveryVersionDespiteWrongSourceArtistAndLowScores()
    {
        _gateway.Setup(item => item.SearchPlayableSongsAsync(It.IsAny<ProtocolExecutionContext>(), "Communicate", 20))
            .ReturnsAsync(Versions());

        var tracks = await SearchAsync("Communicate");

        AssertVersions(tracks);
        Assert.Contains(tracks, item => item.GetProperty("confidence").GetDouble() < .72);
        _gateway.Verify(item => item.SearchPlayableSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 20), Times.Once);
    }

    [Theory]
    [InlineData("Communicate")]
    [InlineData("Other Song who do we blame")]
    [InlineData("Communicate live")]
    [InlineData("who do we blame")]
    public async Task EmptyTitleOrCustomQueryDoesNotRetryTheSnapshotTitle(string query)
    {
        _gateway.Setup(item => item.SearchPlayableSongsAsync(It.IsAny<ProtocolExecutionContext>(), query, 20))
            .ReturnsAsync([]);

        Assert.Empty(await SearchAsync(query));

        _gateway.Verify(item => item.SearchPlayableSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 20), Times.Once);
    }

    [Fact]
    public async Task ExistingSourceQueryResultsDoNotTriggerTheTitleFallback()
    {
        const string query = "Communicate who do we blame";
        _gateway.Setup(item => item.SearchPlayableSongsAsync(It.IsAny<ProtocolExecutionContext>(), query, 20))
            .ReturnsAsync(Versions());

        AssertVersions(await SearchAsync(query));

        _gateway.Verify(item => item.SearchPlayableSongsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), 20), Times.Once);
    }

    [Fact]
    public async Task SourceQueryStillRetriesMissingSourcesWhenAnotherSourceHasResults()
    {
        const string query = "Communicate who do we blame";
        var other = new Song { ExternalProvider = "other", ExternalId = "original", Title = "Communicate", Artist = "who do we blame", Duration = 115 };
        _gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["other", "artistgrid"]);
        _gateway.Setup(item => item.SearchPlayableSongsAsync(It.IsAny<ProtocolExecutionContext>(), query, 20))
            .ReturnsAsync([other]);
        _gateway.Setup(item => item.SearchPlayableSongsAsync(It.IsAny<ProtocolExecutionContext>(), "Communicate", 20))
            .ReturnsAsync([other, .. Versions()]);

        var tracks = await SearchAsync(query);

        Assert.Equal(5, tracks.Length);
        Assert.Single(tracks, item => item.GetProperty("externalProvider").GetString() == "other");
        AssertVersions(tracks.Where(item => item.GetProperty("externalProvider").GetString() == "artistgrid").ToArray());
        _gateway.Verify(item => item.SearchPlayableSongsAsync(It.IsAny<ProtocolExecutionContext>(), "Communicate", 20), Times.Once);
    }

    [Fact]
    public async Task PowerTripEqualRecordingScoresUseAppleAliasPriorityThenExactAlbumEdition()
    {
        var source = await PowerTripSourceAsync("IsExplicit", true);
        _gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["apple-musickit", "deezer", "amazon"]);
        _gateway.Setup(item => item.SearchPlayableSongsAsync(It.IsAny<ProtocolExecutionContext>(), "Power Trip", 20))
            .ReturnsAsync([
                PowerTrip("amazon", "amazon", "Born Sinner (Deluxe Version)"),
                PowerTrip("deezer", "deezer", "Born Sinner (Deluxe Version)", title: "Power Trip"),
                PowerTrip("a-standard", "apple-download", "Born Sinner"),
                PowerTrip("z-deluxe", "apple-download", "Born Sinner (Deluxe Version)")
            ]);

        var tracks = await SearchAsync("Power Trip", source);

        Assert.Equal(new[] { "z-deluxe", "a-standard", "deezer", "amazon" }, tracks.Select(item => item.GetProperty("externalId").GetString()));
        Assert.All(tracks, item => Assert.Equal(.9545, item.GetProperty("confidence").GetDouble()));
        Assert.Equal(1, tracks[0].GetProperty("routingPriority").GetInt32());
        Assert.Equal(1, tracks[0].GetProperty("components").GetProperty("routingPriority").GetInt32());
        Assert.True(tracks[0].GetProperty("matchesSourceAlbum").GetBoolean());
        Assert.False(tracks[1].GetProperty("matchesSourceAlbum").GetBoolean());
        Assert.True(tracks[2].GetProperty("matchesSourceAlbum").GetBoolean());
        Assert.All(tracks, item => Assert.Equal(JsonValueKind.Null, item.GetProperty("isrc").ValueKind));
    }

    [Theory]
    [InlineData("IsExplicit", true)]
    [InlineData("isExplicit", true)]
    [InlineData("IsExplicit", false)]
    [InlineData("isExplicit", false)]
    [InlineData("IsExplicit", null)]
    [InlineData("isExplicit", null)]
    public async Task SourceExplicitStateIsStrictTriStateAndKeepsCleanEditEvidence(string field, bool? explicitSource)
    {
        var source = await PowerTripSourceAsync(field, explicitSource);
        _gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["apple-musickit"]);
        _gateway.Setup(item => item.SearchPlayableSongsAsync(It.IsAny<ProtocolExecutionContext>(), "Power Trip", 20))
            .ReturnsAsync([PowerTrip("clean", "apple-download", "Born Sinner (Deluxe Version)", 0), PowerTrip("explicit", "apple-download", "Born Sinner (Deluxe Version)")]);

        var tracks = await SearchAsync("Power Trip", source);

        var clean = Assert.Single(tracks, item => item.GetProperty("externalId").GetString() == "clean");
        var explicitTrack = Assert.Single(tracks, item => item.GetProperty("externalId").GetString() == "explicit");
        if (explicitSource == true)
        {
            Assert.Equal(0, clean.GetProperty("confidence").GetDouble());
            Assert.Contains(clean.GetProperty("reasons").EnumerateArray(), item => item.GetString() == "clean_edit_of_explicit");
            Assert.True(explicitTrack.GetProperty("confidence").GetDouble() > clean.GetProperty("confidence").GetDouble());
        }
        else if (explicitSource == false)
            Assert.True(clean.GetProperty("confidence").GetDouble() > explicitTrack.GetProperty("confidence").GetDouble());
        else Assert.Equal(clean.GetProperty("confidence").GetDouble(), explicitTrack.GetProperty("confidence").GetDouble());
        Assert.False(clean.GetProperty("isExplicit").GetBoolean());
        Assert.True(explicitTrack.GetProperty("isExplicit").GetBoolean());
        Assert.Equal(2, tracks.Length);
    }

    [Theory]
    [InlineData("IsExplicit", "true")]
    [InlineData("isExplicit", 1)]
    [InlineData("unrecognizedExplicit", true)]
    public async Task UnrecognizedSourceExplicitEvidenceStaysUnknown(string field, object value)
    {
        var source = await PowerTripSourceAsync(field, value);
        _gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["apple-musickit"]);
        _gateway.Setup(item => item.SearchPlayableSongsAsync(It.IsAny<ProtocolExecutionContext>(), "Power Trip", 20))
            .ReturnsAsync([PowerTrip("clean", "apple-download", "Born Sinner", 0), PowerTrip("unknown", "apple-download", "Born Sinner", 2)]);
        var tracks = await SearchAsync("Power Trip", source);
        Assert.Equal(tracks[0].GetProperty("confidence").GetDouble(), tracks[1].GetProperty("confidence").GetDouble());
        Assert.Equal(JsonValueKind.Null, Assert.Single(tracks, item => item.GetProperty("externalId").GetString() == "unknown").GetProperty("isExplicit").ValueKind);
    }

    [Fact]
    public async Task RecordingEvidenceOutranksProviderPriorityAndAlbumEdition()
    {
        var source = await PowerTripSourceAsync("IsExplicit", true);
        var stronger = PowerTrip("stronger", "deezer", "Born Sinner");
        var preferred = PowerTrip("preferred", "apple-download", "Born Sinner (Deluxe Version)");
        preferred.Title = "Power Trip live";
        _gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["apple-musickit", "deezer"]);
        _gateway.Setup(item => item.SearchPlayableSongsAsync(It.IsAny<ProtocolExecutionContext>(), "Power Trip", 20)).ReturnsAsync([preferred, stronger]);
        var tracks = await SearchAsync("Power Trip", source);
        Assert.Equal("stronger", tracks[0].GetProperty("externalId").GetString());
        Assert.True(tracks[0].GetProperty("confidence").GetDouble() > tracks[1].GetProperty("confidence").GetDouble());
    }

    [Fact]
    public async Task MissingSourceScoresAndAlbumEvidenceStayNullWhileOrderIsDeterministic()
    {
        _gateway.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming)).Returns(["apple-musickit", "deezer"]);
        _gateway.Setup(item => item.SearchPlayableSongsAsync(It.IsAny<ProtocolExecutionContext>(), "Power Trip", 20))
            .ReturnsAsync([PowerTrip("deezer", "deezer", "Born Sinner"), PowerTrip("z", "apple-download", ""), PowerTrip("A", "apple-download", "")]);
        var tracks = await SearchAsync("Power Trip", Guid.Empty);
        Assert.Equal(new[] { "A", "z", "deezer" }, tracks.Select(item => item.GetProperty("externalId").GetString()));
        Assert.All(tracks, item => Assert.Equal(JsonValueKind.Null, item.GetProperty("confidence").ValueKind));
        Assert.All(tracks, item => Assert.Equal(JsonValueKind.Null, item.GetProperty("matchesSourceAlbum").ValueKind));
    }

    [Fact]
    public async Task InteractiveMediaUsesOnlyReportedFactsAndNeverChangesRecordingScoreOrFetchesAudio()
    {
        var source = await PowerTripSourceAsync("IsExplicit", null);
        var lossless = PowerTrip("lossless", "artistgrid", "Born Sinner");
        lossless.Codec = " FLAC "; lossless.Container = " FLAC "; lossless.BitDepth = 24; lossless.SampleRate = 96000;
        lossless.Bitrate = 2900000; lossless.Channels = 2;
        var lossy = PowerTrip("lossy", "artistgrid", "Born Sinner");
        lossy.Codec = " AAC "; lossy.Container = " M4A "; lossy.BitDepth = 24; lossy.SampleRate = 44100;
        lossy.Bitrate = 256000;
        var unknown = PowerTrip("unknown", "artistgrid", "Born Sinner");
        unknown.Codec = "unknown"; unknown.Container = " "; unknown.Bitrate = 0; unknown.SampleRate = -1; unknown.BitDepth = 0;
        var unsafeLabel = PowerTrip("unsafe", "artistgrid", "Born Sinner");
        unsafeLabel.Codec = "https://media.example.test/private?token=fixture"; unsafeLabel.Container = "cookie=fixture";
        _gateway.Setup(item => item.SearchPlayableSongsAsync(It.IsAny<ProtocolExecutionContext>(), "Power Trip", 20))
            .ReturnsAsync([lossless, lossy, unknown, unsafeLabel]);
        var tracks = await SearchAsync("Power Trip", source);
        Assert.Single(tracks.Select(item => item.GetProperty("confidence").GetDouble()).Distinct());
        var flac = Assert.Single(tracks, item => item.GetProperty("externalId").GetString() == "lossless").GetProperty("media");
        Assert.Equal("flac", flac.GetProperty("codec").GetString());
        Assert.Equal("flac", flac.GetProperty("container").GetString());
        Assert.Equal(24, flac.GetProperty("bitDepth").GetInt32());
        Assert.Equal(96000, flac.GetProperty("sampleRate").GetInt32());
        Assert.Equal(2900000, flac.GetProperty("bitrate").GetInt32());
        Assert.Equal(2, flac.GetProperty("channels").GetInt32());
        var aac = Assert.Single(tracks, item => item.GetProperty("externalId").GetString() == "lossy").GetProperty("media");
        Assert.Equal("aac", aac.GetProperty("codec").GetString());
        Assert.Equal("m4a", aac.GetProperty("container").GetString());
        Assert.Equal(JsonValueKind.Null, aac.GetProperty("bitDepth").ValueKind);
        Assert.Equal(JsonValueKind.Null, Assert.Single(tracks, item => item.GetProperty("externalId").GetString() == "unknown").GetProperty("media").ValueKind);
        Assert.Equal(JsonValueKind.Null, Assert.Single(tracks, item => item.GetProperty("externalId").GetString() == "unsafe").GetProperty("media").ValueKind);
        Assert.DoesNotContain("private?token", JsonSerializer.Serialize(tracks));
        _gateway.Verify(item => item.SearchPlayableSongsAsync(It.IsAny<ProtocolExecutionContext>(), "Power Trip", 20), Times.Once);
        _gateway.Verify(item => item.GetProviderOrder(ProviderCapabilityKind.Streaming), Times.Once);
        _gateway.Verify(item => item.GetProviderOrder(ProviderCapabilityKind.Download), Times.Once);
        _gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LocalInteractiveTargetsKeepRankZeroAndTheirUnknownExplicitAndMediaFacts()
    {
        var source = await PowerTripSourceAsync("IsExplicit", true);
        var standard = new LibraryTrackRecord
        {
            Id = Guid.NewGuid(),
            OwnerUserId = _user,
            BackendInstanceId = "fixture",
            BackendItemId = "a-standard",
            Title = "Power Trip",
            Artist = "J. Cole",
            Album = "Born Sinner",
            DurationMilliseconds = 241000
        };
        var deluxe = new LibraryTrackRecord
        {
            Id = Guid.NewGuid(),
            OwnerUserId = _user,
            BackendInstanceId = "fixture",
            BackendItemId = "z-deluxe",
            Title = "Power Trip (feat. Miguel)",
            Artist = "J. Cole",
            Album = "Born Sinner (Deluxe Version)",
            DurationMilliseconds = 241000
        };
        var repository = new Mock<ITrackMatchRepository>(MockBehavior.Strict);
        repository.Setup(item => item.SearchLocalTracksAsync(new TrackMatchActor(_user, false), "Power Trip", null, 20,
                It.Is<ExternalTrackMatchSnapshot>(snapshot => snapshot.IsExplicit == true), It.IsAny<CancellationToken>()))
            .ReturnsAsync([standard, deluxe]);
        var controller = Controller(repository.Object);
        var result = Assert.IsType<OkObjectResult>(await controller.SearchLocalTargets("Power Trip", externalSnapshotId: source));
        var tracks = JsonSerializer.SerializeToElement(result.Value).GetProperty("tracks").EnumerateArray().ToArray();
        Assert.Equal(new[] { "z-deluxe", "a-standard" }, tracks.Select(item => item.GetProperty("backendItemId").GetString()));
        Assert.All(tracks, item =>
        {
            Assert.Equal(0, item.GetProperty("routingPriority").GetInt32());
            Assert.Equal(JsonValueKind.Null, item.GetProperty("isExplicit").ValueKind);
            Assert.Equal(JsonValueKind.Null, item.GetProperty("media").ValueKind);
        });
        repository.VerifyAll();
        _gateway.VerifyNoOtherCalls();
    }

    private async Task<Guid> PowerTripSourceAsync(string explicitField, object? value)
    {
        var id = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["Title"] = "Power Trip (feat. Miguel)",
            ["Artists"] = new[] { "J. Cole", "Miguel" },
            ["Album"] = "Born Sinner (Deluxe Version)",
            ["DurationMilliseconds"] = 241160,
            ["Isrc"] = null,
            [explicitField] = value
        });
        await using var db = _factory.CreateDbContext();
        db.ExternalMetadataSnapshots.Add(new()
        {
            Id = id,
            OwnerUserId = _user,
            ProviderId = "spotify",
            ResourceKind = "track",
            Protocol = "jellyfin",
            BackendInstanceId = "fixture",
            BackendPrincipalId = "listener",
            ExternalIdHash = id.ToString("N") + id.ToString("N"),
            PayloadJson = payload,
            PayloadSha256 = new string('d', 64),
            SnapshotVersion = 1,
            ProviderRevision = "1",
            RetrievedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static Song PowerTrip(string id, string provider, string album, int? explicitContent = 1, string? title = null) => new()
    {
        ExternalId = id,
        ExternalProvider = provider,
        Title = title ?? "Power Trip (feat. Miguel)",
        Artist = "J. Cole",
        Artists = ["J. Cole", "Miguel"],
        Album = album,
        Duration = 241,
        ExplicitContentLyrics = explicitContent
    };

    private async Task<JsonElement[]> SearchAsync(string query, Guid? snapshot = null)
    {
        var controller = Controller();
        var result = Assert.IsType<OkObjectResult>(await controller.SearchProviderTargets(query, snapshot ?? _snapshot));
        return JsonSerializer.SerializeToElement(result.Value).GetProperty("tracks").EnumerateArray().ToArray();
    }

    private TrackMatchesController Controller(ITrackMatchRepository? repository = null)
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
            AllstarrUserId = _user,
            JellyfinAccessToken = "fixture",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        return new TrackMatchesController(
            repository ?? Mock.Of<ITrackMatchRepository>(), _gateway.Object,
            new AdminProtocolExecutionContextFactory(_factory, new SystemPlatformClock()),
            _factory, null!, null!, new TrackMatchDecisionEngine(), null!)
        { ControllerContext = new() { HttpContext = http } };
    }

    private static Song[] Versions() =>
    [
        Version("v1", "Diva Shit [V1]", 156),
        Version("v2", "Communicate [V2]", 115),
        Version("v3", "Communicate [V3] [OG File]", 115),
        Version("v4", "Communicate [V4] [OG File]", 115)
    ];

    private static Song Version(string id, string title, int duration) => new()
    {
        ExternalProvider = "artistgrid",
        ExternalId = id,
        Title = title,
        Artist = "Ariana Grande",
        Album = "Unreleased",
        Duration = duration
    };

    private static void AssertVersions(JsonElement[] tracks)
    {
        Assert.Equal(new[] { "v1", "v2", "v3", "v4" }, tracks.Select(item => item.GetProperty("externalId").GetString()).Order().ToArray());
        var first = Assert.Single(tracks, item => item.GetProperty("externalId").GetString() == "v1");
        Assert.Equal("Diva Shit [V1]", first.GetProperty("title").GetString());
        Assert.Equal(156000, first.GetProperty("durationMilliseconds").GetInt32());
        Assert.Equal("v1", tracks[^1].GetProperty("externalId").GetString());
        foreach (var item in tracks.Where(item => item.GetProperty("externalId").GetString() != "v1"))
        {
            Assert.Equal(115000, item.GetProperty("durationMilliseconds").GetInt32());
            Assert.Equal(1, item.GetProperty("components").GetProperty("duration").GetDouble());
        }
        Assert.All(tracks, item => Assert.Equal("Ariana Grande", item.GetProperty("artist").GetString()));
    }

    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
