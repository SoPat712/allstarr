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

    private async Task<JsonElement[]> SearchAsync(string query)
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
        var controller = new TrackMatchesController(
            Mock.Of<ITrackMatchRepository>(), _gateway.Object,
            new AdminProtocolExecutionContextFactory(_factory, new SystemPlatformClock()),
            _factory, null!, null!, new TrackMatchDecisionEngine(), null!)
        { ControllerContext = new() { HttpContext = http } };
        var result = Assert.IsType<OkObjectResult>(await controller.SearchProviderTargets(query, _snapshot));
        return JsonSerializer.SerializeToElement(result.Value).GetProperty("tracks").EnumerateArray().ToArray();
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
