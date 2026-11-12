using allstarr.Core.Artwork;
using System.Net;
using allstarr.Controllers.ClientApi;
using allstarr.Core.Capabilities;
using allstarr.Core.ClientApi;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Models.Lyrics;
using allstarr.Models.Settings;
using allstarr.Services.Jellyfin;
using allstarr.Services.Local;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed class ClientApiItemLyricsTests
{
    [Theory]
    [InlineData("103060238", "Too Many")]
    [InlineData("1031579282", "Lightbeam (feat. NoCap)")]
    public async Task ExternalSongWithoutStoredSnapshotResolvesLyricsAndSources(string externalId, string title)
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var factory = new Factory(database.Options);
        var fixture = new Fixture();
        await using (var db = factory.CreateDbContext())
        {
            db.Users.Add(new UserRecord
            {
                Id = fixture.Context.Principal!.UserId,
                BackendType = "jellyfin",
                BackendInstanceId = "fixture",
                BackendPrincipalId = "listener",
                DisplayName = "Listener",
                Enabled = true
            });
            await db.SaveChangesAsync();
        }
        var storage = new DurableStorageState(database.StorageOptions);
        storage.Set(DurableStorageReadiness.Ready, "fixture");
        var identities = new TrackIdentityService(factory, storage, new Clock());
        var itemId = $"ext-deezer-song-{externalId}";
        fixture.ExternalSong(itemId, "deezer", externalId, title);
        Assert.Null(await identities.ResolveRecordingAsync(fixture.Context.RequireActor(), itemId));

        var controller = fixture.Controller(identities);
        Assert.IsType<RichLyrics>(Assert.IsType<OkObjectResult>((await controller.Get(itemId)).Result).Value);
        var sources = Assert.IsType<ClientLyricsSources>(Assert.IsType<OkObjectResult>((await controller.Sources(itemId)).Result).Value);

        Assert.Equal(["ready", "pending"], sources.Sources.Select(source => source.State));
        Assert.Equal("lrclib", sources.Chosen?.SourceId);
        Assert.Equal("automatic", sources.Chosen?.Layer);
        Assert.Equal(1, sources.Sources[0].Rank);
        Assert.Contains("plain_text", sources.Sources[0].Reasons!);
        Assert.All(sources.Sources.Skip(1), source =>
        {
            Assert.Null(source.Score);
            Assert.Null(source.Rank);
            Assert.Null(source.Reasons);
        });
        Assert.Equal(new ClientLyricsRevision(), sources.Revision);
        fixture.AssertSameLookup(itemId, $"deezer:{externalId}", title);
        await using var stored = factory.CreateDbContext();
        Assert.Empty(await stored.ExternalMetadataSnapshots.ToArrayAsync());
        Assert.Empty(await stored.CanonicalRecordings.ToArrayAsync());
        Assert.Empty(await stored.ProviderTrackIdentities.ToArrayAsync());
    }

    [Theory]
    [InlineData("deezer", "103060238")]
    [InlineData("spotify", "fixture")]
    public async Task ResolvedProviderMetadataDoesNotRequireAnotherCatalogLookup(string provider, string externalId)
    {
        var fixture = new Fixture();
        var itemId = $"ext-{provider}-song-{externalId}";
        var recording = Guid.NewGuid();
        fixture.ExternalSong(itemId, provider, externalId, "Song", recording);
        var controller = fixture.Controller();

        Assert.IsType<OkObjectResult>((await controller.Get(itemId)).Result);
        Assert.IsType<OkObjectResult>((await controller.Sources(itemId)).Result);
        fixture.AssertSameLookup(itemId, $"{provider}:{externalId}", "Song");
        fixture.Providers.Verify(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "allstarr", It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableRecordingIdentityDoesNotHideResolvedSongSources(bool forbidden)
    {
        var fixture = new Fixture();
        const string itemId = "ext-deezer-song-103060238";
        fixture.ExternalSong(itemId, "deezer", "103060238", "Song");
        fixture.Identities.Setup(item => item.ResolveRecordingAsync(It.IsAny<ProviderActorContext>(), itemId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(forbidden ? new UnauthorizedAccessException() : new KeyNotFoundException());
        var controller = fixture.Controller();

        Assert.IsType<OkObjectResult>((await controller.Get(itemId)).Result);
        Assert.IsType<OkObjectResult>((await controller.Sources(itemId)).Result);
    }

    [Fact]
    public async Task LibrarySongWithoutIndexedRecordingUsesTheSameBackendLyricsLookup()
    {
        var fixture = new Fixture();
        var controller = fixture.Controller();
        Assert.IsType<OkObjectResult>((await controller.Get("native-song")).Result);
        var result = Assert.IsType<ClientLyricsSources>(Assert.IsType<OkObjectResult>((await controller.Sources("native-song")).Result).Value);
        Assert.Equal("ready", result.Sources[0].State);
        fixture.AssertSameLookup("native-song", "native-song", "Native song");
        Assert.All(fixture.Lookups, lookup => Assert.NotNull(lookup.Sources?.Backend));
        var backend = await fixture.Lookups[1].Sources!.Backend!(fixture.Context.CancellationToken);
        Assert.Equal("Native words", backend?.PlainLyrics);
    }

    [Fact]
    public async Task UnknownSongRemainsMissingFromBothEndpoints()
    {
        var fixture = new Fixture();
        fixture.Local.Setup(item => item.ParseSongId("ext-deezer-song-missing")).Returns((true, "deezer", "missing"));
        var controller = fixture.Controller();
        Assert.IsType<NotFoundResult>((await controller.Get("ext-deezer-song-missing")).Result);
        Assert.IsType<NotFoundResult>((await controller.Sources("ext-deezer-song-missing")).Result);
        Assert.Empty(fixture.Lookups);
    }

    [Theory]
    [InlineData(false, "personal")]
    [InlineData(true, "household")]
    public async Task SourcesPreserveSavedVersionChoiceAndRevision(bool household, string layer)
    {
        var fixture = new Fixture();
        var recording = Guid.NewGuid();
        const string itemId = "ext-deezer-song-103060238";
        fixture.ExternalSong(itemId, "deezer", "103060238", "Song", recording);
        var choice = new RecordingLyricsChoice(Guid.NewGuid(), recording, household ? null : fixture.Context.Principal!.UserId, "lrclib:alternate", 3);
        var layers = new RecordingLyricsChoiceLayers(household ? null : choice, household ? choice : null);
        fixture.Choices.Setup(item => item.GetAsync(It.IsAny<ProtocolExecutionContext>(), recording)).ReturnsAsync(layers);
        fixture.Candidates = [
            new("lrclib", new LyricsInfo { Source = "lrclib", SyncedLyrics = "[00:01]Default" }),
            new("lrclib:alternate", new LyricsInfo
            {
                Source = "lrclib:alternate", PlainLyrics = "Alternate words",
                Metadata = new("alternate", "Alternate song", "Artist", "Album", 120000)
            }) { IsDefaultVersion = false }
        ];

        var result = Assert.IsType<ClientLyricsSources>(Assert.IsType<OkObjectResult>((await fixture.Controller().Sources(itemId)).Result).Value);
        Assert.Equal(new ClientLyricsChosen("lrclib:alternate", layer), result.Chosen);
        Assert.Equal(2, result.Sources[1].Rank);
        Assert.Equal(1, result.Sources[0].Rank);
        Assert.Equal(ClientLyricsRevision.From(layers.Revision), result.Revision);
        var alternate = result.Sources[1];
        Assert.Equal("plain", alternate.Format);
        Assert.Equal("Alternate words", alternate.Preview);
        Assert.Equal("Alternate song", alternate.Title);
        Assert.Equal("Artist", alternate.Artist);
        Assert.Equal("Album", alternate.Album);
        Assert.Equal(120000, alternate.DurationMs);
        Assert.Equal("lrclib", alternate.ProviderId);
    }

    [Fact]
    public async Task SourcesRankEveryReadyVersionAndChooseRankOneWhenNoPreferenceIsStored()
    {
        var fixture = new Fixture();
        const string itemId = "ext-deezer-song-103060238";
        fixture.ExternalSong(itemId, "deezer", "103060238", "Song");
        fixture.Candidates = [
            new("lrclib", new LyricsInfo { Source = "lrclib", PlainLyrics = "Default words" }),
            new("lrclib:alternate", new LyricsInfo { Source = "lrclib:alternate", SyncedLyrics = "[00:01]<00:01>Alternate <00:02>words" }) { IsDefaultVersion = false },
            new("lrcred", new LyricsInfo { Source = "lrcred", SyncedLyrics = "[00:01]Default timed words" })
        ];

        var result = Assert.IsType<ClientLyricsSources>(Assert.IsType<OkObjectResult>((await fixture.Controller().Sources(itemId)).Result).Value);
        Assert.Equal(new ClientLyricsChosen("lrclib:alternate", "automatic"), result.Chosen);
        Assert.Equal(["lrclib:alternate", "lrcred", "lrclib"], result.Sources.Select(source => source.SourceId));
        Assert.Equal([1, 2, 3], result.Sources.Select(source => source.Rank));
        Assert.All(result.Sources, source => Assert.InRange(source.Score!.Value, 0, 1));
        Assert.Contains("synced_words", result.Sources[0].Reasons!);
    }

    [Fact]
    public async Task SourcesScoreAutomaticAndManualCandidatesAgainstTheRecordingDuration()
    {
        var fixture = new Fixture();
        var recording = Guid.NewGuid();
        const string itemId = "ext-deezer-song-duration";
        fixture.ExternalSong(itemId, "deezer", "duration", "Song", recording);
        fixture.Providers.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "duration"))
            .ReturnsAsync(new Song
            {
                Id = itemId,
                Title = "Song",
                Artist = "Artist",
                Duration = 10,
                CanonicalRecordingId = recording,
                ExternalProvider = "deezer",
                ExternalId = "duration"
            });
        fixture.Identities.Setup(item => item.ResolveRecordingAsync(It.IsAny<ProviderActorContext>(), recording.ToString("D"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CanonicalRecordingIdentity(recording, fixture.Context.Principal!.UserId, null, null, 1) { DurationMilliseconds = 120_000 });
        fixture.Candidates = [
            new("lrclib", new LyricsInfo { Source = "lrclib", SyncedLyrics = "[01:50]Last line" })
            {
                Assessment = new LyricsScore(0.35, ["synced_lines", "duration_mismatch"])
            },
            new("genius", new LyricsInfo { Source = "genius", SyncedLyrics = "[00:08]<00:08>Short <00:09>words" })
            {
                AutomaticEligible = false
            }
        ];

        var result = Assert.IsType<ClientLyricsSources>(Assert.IsType<OkObjectResult>((await fixture.Controller().Sources(itemId)).Result).Value);

        Assert.Equal(["lrclib", "genius"], result.Sources.Select(source => source.SourceId));
        Assert.Equal([1, 2], result.Sources.Select(source => source.Rank));
        Assert.Contains("duration_fits", result.Sources[0].Reasons!);
        Assert.Contains("duration_mismatch", result.Sources[1].Reasons!);
        Assert.True(result.Sources[0].Score!.Value > result.Sources[1].Score!.Value);
        Assert.Equal(new ClientLyricsChosen("lrclib", "automatic"), result.Chosen);
    }

    [Fact]
    public async Task SourcesOmitLyricsWithoutWords()
    {
        var fixture = new Fixture();
        const string itemId = "ext-deezer-song-wordless";
        fixture.ExternalSong(itemId, "deezer", "wordless", "Song");
        var wordless = new LyricsInfo { Source = "lrclib", SyncedLyrics = "[00:01]♪" };
        fixture.ResolvedLyrics = wordless;
        fixture.Candidates = [new("lrclib", wordless)];

        var sources = Assert.IsType<ClientLyricsSources>(Assert.IsType<OkObjectResult>((await fixture.Controller().Sources(itemId)).Result).Value);
        Assert.Empty(sources.Sources);
        Assert.Null(sources.Chosen);
    }

    [Fact]
    public async Task SourceLookupCancellationReachesTheCaller()
    {
        var fixture = new Fixture();
        const string itemId = "ext-deezer-song-103060238";
        fixture.ExternalSong(itemId, "deezer", "103060238", "Song");
        fixture.Identities.Setup(item => item.ResolveRecordingAsync(It.IsAny<ProviderActorContext>(), itemId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Controller().Sources(itemId));
    }

    private sealed class Fixture
    {
        public ProtocolExecutionContext Context { get; } = new(ProtocolKind.Jellyfin, "fixture", "listener",
            new AllstarrPrincipal(Guid.NewGuid(), "jellyfin", "fixture", "listener", "Listener", false),
            "item-lyrics", DateTimeOffset.UtcNow.AddMinutes(1), default);
        public Mock<ILocalLibraryService> Local { get; } = new();
        public Mock<IProtocolProviderGateway> Providers { get; } = new();
        public Mock<ITrackIdentityService> Identities { get; } = new();
        public Mock<ILibraryIndexService> Library { get; } = new();
        public Mock<IRecordingLyricsChoices> Choices { get; } = new();
        public LyricsInfo ResolvedLyrics { get; set; } = new() { Source = "lrclib", PlainLyrics = "Words" };
        public IReadOnlyList<ProtocolLyricsCandidate> Candidates { get; set; } = [
            new("lrclib", new LyricsInfo { Source = "lrclib", PlainLyrics = "Words" }),
            new("lyricsplus", null) { State = "pending" },
            new("genius", null)
        ];
        public List<(ProtocolExecutionContext Context, Song Song, string Key, ProtocolLyricsSources? Sources)> Lookups { get; } = [];
        private readonly Mock<IProtocolLyricsResolver> _lyrics = new(MockBehavior.Strict);

        public Fixture()
        {
            Choices.Setup(item => item.GetAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Guid>()))
                .ReturnsAsync(new RecordingLyricsChoiceLayers(null, null));
            _lyrics.Setup(item => item.GetRichLyricsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>(), It.IsAny<string>(), It.IsAny<ProtocolLyricsSources?>()))
                .Returns((ProtocolExecutionContext context, Song song, string key, ProtocolLyricsSources? sources) =>
                {
                    Lookups.Add((context, song, key, sources));
                    return Task.FromResult(RichLyricsMapper.Map(ResolvedLyrics));
                });
            _lyrics.Setup(item => item.FindAllAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>(), It.IsAny<string>(), It.IsAny<ProtocolLyricsSources?>()))
                .Returns((ProtocolExecutionContext context, Song song, string key, ProtocolLyricsSources? sources) =>
                {
                    Lookups.Add((context, song, key, sources));
                    return Task.FromResult(Candidates);
                });
        }

        public void ExternalSong(string itemId, string provider, string externalId, string title, Guid? recording = null)
        {
            Local.Setup(item => item.ParseSongId(itemId)).Returns((true, provider, externalId));
            Providers.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), provider, externalId))
                .ReturnsAsync(new Song { Id = itemId, Title = title, Artist = "Artist [E]", CanonicalRecordingId = recording });
        }

        public ItemLyricsController Controller(ITrackIdentityService? identities = null)
        {
            identities ??= Identities.Object;
            var clients = new Mock<IHttpClientFactory>();
            clients.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(new HttpClient(new BackendHandler()));
            var http = new DefaultHttpContext();
            http.Request.Headers["X-Emby-Token"] = "fixture-token";
            http.Items[ProtocolExecutionContextFactory.HttpContextItemKey] = Context;
            var cache = new DisabledApplicationCache();
            var backend = new JellyfinProxyService(clients.Object, Options.Create(new JellyfinSettings { Url = "http://backend.invalid" }),
                new HttpContextAccessor { HttpContext = http }, NullLogger<JellyfinProxyService>.Instance, cache,
                new MediaAssetResolver(cache, NullLogger<MediaAssetResolver>.Instance), new ConfigurationBuilder().Build());
            var review = new RecordingLyricsReview(identities, Providers.Object, Library.Object, _lyrics.Object, Choices.Object);
            var reader = new ItemLyricsReader(Local.Object, backend, Providers.Object, _lyrics.Object, Library.Object, identities, review, Choices.Object);
            return new(reader) { ControllerContext = new() { HttpContext = http } };
        }

        public void AssertSameLookup(string itemId, string key, string title)
        {
            Assert.Equal(2, Lookups.Count);
            Assert.All(Lookups, lookup =>
            {
                Assert.Equal(itemId, lookup.Context.RequestedItemId);
                Assert.Equal(Context.Deadline, lookup.Context.Deadline);
                Assert.Equal(Context.CancellationToken, lookup.Context.CancellationToken);
                Assert.Equal(key, lookup.Key);
                Assert.Equal(title, lookup.Song.Title);
            });
            Assert.Equal(Lookups[0].Song.ExternalProvider, Lookups[1].Song.ExternalProvider);
            Assert.Equal(Lookups[0].Song.ExternalId, Lookups[1].Song.ExternalId);
            Assert.Equal(Lookups[0].Song.Artist, Lookups[1].Song.Artist);
        }
    }

    private sealed class BackendHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri!.AbsolutePath.EndsWith("/Lyrics", StringComparison.Ordinal)
                ? """{"Metadata":{"IsSynced":false},"Lyrics":[{"Text":"Native words"}]}"""
                : """{"Items":[{"Id":"native-song","Name":"Native song","Type":"Audio"}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private sealed class Clock : IPlatformClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
