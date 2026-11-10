using System.Collections.Immutable;
using allstarr.Core.Capabilities;
using allstarr.Core.ClientApi;
using allstarr.Core.Identity;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using Moq;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Models.Domain;

namespace allstarr.Tests;

public sealed class LyricsSearchIndexTests
{
    [Fact]
    public async Task LyricsSearch_SpecificPhraseKeepsRankedPartialMatchesAndBestLine()
    {
        var root = Path.Combine(Path.GetTempPath(), "lyrics-ranking-" + Guid.NewGuid().ToString("N"));
        var context = Context();
        try
        {
            using var index = new LyricsSearchIndex(new StorageOptions { DataDirectory = root });
            await index.StoreAsync(context, Song(1), null, Lyrics("Constellation number 19999"));
            await index.StoreAsync(context, Song(2), null, Lyrics("19999 under a constellation number"));
            await index.StoreAsync(context, Song(3), null, Lyrics("A different story\nConstellation number 42"));
            await index.StoreAsync(context, Song(4), null, Lyrics("Just a number"));
            await index.StoreAsync(context, Song(5), null, Lyrics("No matching words"));

            var result = await index.SearchAsync(context, "constellation number 19999", 3,
                await index.SnapshotAsync(context), Policy(), new Dictionary<Guid, string>());

            Assert.Equal(new[] { "song-1", "song-2", "song-3" }, result.Results.Select(hit => hit.ItemId));
            Assert.Equal(new[] { 1.0, 0.8, 0.2 + 0.5 * 2 / 3 }, result.Results.Select(hit => hit.Score));
            Assert.Equal("Constellation number 42", result.Results[2].Line.Text);
            Assert.True(result.Truncated);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task LyricsSearch_DoesNotReassignFetchedPrivateContentToANewAccountRevision()
    {
        var root = Path.Combine(Path.GetTempPath(), "lyrics-binding-" + Guid.NewGuid().ToString("N"));
        var account = Guid.NewGuid();
        var capability = new ProviderCapabilityDescriptor(ProviderCapabilityKind.Lyrics, ProviderCapabilitySupportState.Supported,
            ProviderAccountRequirement.Required, "1", ["fetchLyrics"], allowedAccountScopes: [ProviderAccountScope.Personal, ProviderAccountScope.Shared]);
        var descriptor = new ProviderDescriptor("musixmatch", "Musixmatch", "Fixture", ProviderOrigin.BuiltIn, "1", "1", [capability], new());
        var router = new Mock<IProviderRouter>();
        router.Setup(service => service.PlanAsync<IProviderLyricsCapability>(It.IsAny<ProviderRouteRequest>())).Returns<ProviderRouteRequest>(request =>
        {
            var execution = new ProviderExecutionContext(request.Actor, "musixmatch", new(account, "musixmatch", ProviderAccountScope.Shared, 2),
                request.Policy, request.OperationId, request.CorrelationId, request.Deadline, request.CancellationToken);
            return Task.FromResult(new ProviderRoutePlan<IProviderLyricsCapability>(request,
                [new(0, descriptor, capability, Mock.Of<IProviderLyricsCapability>(), execution, null)],
                new(request.CorrelationId, request.Capability, "musixmatch", null, [])));
        });
        try
        {
            using var index = new LyricsSearchIndex(new StorageOptions { DataDirectory = root }, router.Object);
            var context = Context();
            var rich = new RichLyrics(new("musixmatch", "Musixmatch"), "plain", Lyrics("Private words").Lines);
            await index.StoreAsync(context, Song(1), null, rich, expectedProvenance: $"{account:N}:1");
            Assert.Empty(await index.SnapshotAsync(context));
            await index.StoreAsync(context, Song(1), null, rich, expectedProvenance: $"{account:N}:2");
            Assert.Equal($"{account:N}:2", Assert.Single(await index.SnapshotAsync(context)).Provenance);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task LyricsSearch_FailedCacheRefreshRetriesWithoutDelayingNewChanges()
    {
        var clock = new SearchClock();
        var changes = new LyricsSearchChanges(clock);
        var user = Guid.NewGuid();
        var recording = Guid.NewGuid();
        changes.RetryRecording(user, recording);
        Assert.Empty(changes.TakeRecordings());
        clock.Now = clock.Now.AddMinutes(1);
        Assert.Single(changes.TakeRecordings());
        changes.RetryRecording(user, recording);
        changes.RecordingChanged(user, recording);
        Assert.Single(changes.TakeRecordings());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task LyricsSearch_CacheChangesSurviveAFullLibraryNotificationQueue()
    {
        var changes = new LyricsSearchChanges();
        for (var index = 0; index < 1000; index++) changes.LibraryChanged(Guid.NewGuid());
        var user = Guid.NewGuid();
        var recording = Guid.NewGuid();
        changes.RecordingChanged(user, recording);
        changes.RecordingChanged(user, recording);
        var pending = Assert.Single(changes.TakeRecordings());
        Assert.Equal(user, pending.UserId);
        Assert.Equal(recording, pending.RecordingId);
        Assert.Empty(changes.TakeRecordings());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task LyricsSearch_IndexReplacesSourcesAndSurvivesReopening()
    {
        var root = Path.Combine(Path.GetTempPath(), "lyrics-search-" + Guid.NewGuid().ToString("N"));
        var options = new StorageOptions { DataDirectory = root };
        var context = Context();
        try
        {
            using (var index = new LyricsSearchIndex(options))
            {
                var recording = Guid.NewGuid();
                await index.StoreAsync(context, new Song { Id = "ext-deezer-song-1", ExternalProvider = "deezer", ExternalId = "1", Title = "External", Artist = "Artist" },
                    recording, Lyrics("Original phrase"));
                await index.StoreAsync(context, Song(1), recording, Lyrics("Original phrase"));
                Assert.Equal("song-1", Assert.Single((await Search(index, context, "original")).Results).ItemId);
                await index.StoreAsync(context, Song(1), null, new(new("unison", "Unison"), "line", Lyrics("Chosen later").Lines));
                Assert.Empty((await Search(index, context, "original")).Results);
                await index.ReconcileStoredAsync(context, Song(1), new HashSet<string>(), new HashSet<string>());
                Assert.Single((await Search(index, context, "chosen later")).Results);
                await index.ReconcileStoredAsync(context, Song(1), new HashSet<string>(), new HashSet<string> { "unison" });
                await index.StoreAsync(context, Song(1), recording, Lyrics("Replacement phrase"));
                Assert.Empty((await Search(index, context, "original")).Results);
                Assert.Single((await Search(index, context, "replacement")).Results);
                Assert.Single((await Search(index, context, "repla")).Results);
                await index.StoreAsync(context, Song(1), null, new(new("kept", "Kept lyrics"), "word", Lyrics("Deleted sidecar").Lines));
                await index.ReconcileStoredAsync(context, Song(1), new HashSet<string>());
                Assert.Empty((await Search(index, context, "deleted")).Results);
            }
            using (var reopened = new LyricsSearchIndex(options))
                Assert.Single((await Search(reopened, context, "replacement")).Results);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task LyricsSearch_PlainSelectedSourceBorrowsNearestVisibleMatchingTiming()
    {
        var root = Path.Combine(Path.GetTempPath(), "lyrics-timing-" + Guid.NewGuid().ToString("N"));
        var context = Context();
        var recording = Guid.NewGuid();
        try
        {
            using var index = new LyricsSearchIndex(new StorageOptions { DataDirectory = root });
            await index.StoreAsync(context, Song(1), recording, Lyrics("Intro\nSecond\nFloating, again!\nFinal"));
            await index.StoreAsync(context, Song(2), recording, new(new("lrclib", "Timed lyrics"), "line",
                [new(1000, 2000, "floating again", "lead", []), new(null, null, "Other", "lead", []),
                    new(null, null, "More", "lead", []), new(10000, 12000, "FLOATING—again!", "lead", [])]));
            var result = await index.SearchAsync(context, "floating again", 20, await index.SnapshotAsync(context), Policy(),
                new Dictionary<Guid, string> { [recording] = "backend" });
            var hit = Assert.Single(result.Results);
            Assert.Equal("song-1", hit.ItemId);
            Assert.Equal("backend", hit.Source.Id);
            Assert.Equal("Floating, again!", hit.Line.Text);
            Assert.Equal(10000, hit.Line.StartMs);
            Assert.Equal(12000, hit.Line.EndMs);
            Assert.Equal("Second", hit.Line.Before);
            Assert.True(hit.Line.Synced);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task LyricsSearch_TimingFallbackDoesNotUseAnotherListenersPrivateLyrics()
    {
        var root = Path.Combine(Path.GetTempPath(), "lyrics-private-timing-" + Guid.NewGuid().ToString("N"));
        var context = Context();
        var foreign = Context();
        var recording = Guid.NewGuid();
        try
        {
            using var index = new LyricsSearchIndex(new StorageOptions { DataDirectory = root });
            await index.StoreAsync(context, Song(1), recording, Lyrics("Private moon"));
            await index.StoreAsync(foreign, Song(1), recording, new(new("kept", "Kept lyrics"), "line",
                [new(1000, 2000, "Private moon", "lead", [])]));
            Assert.Single(await index.SnapshotAsync(foreign));
            var hit = Assert.Single((await Search(index, context, "private moon")).Results);
            Assert.Null(hit.Line.StartMs);
            Assert.Null(hit.Line.EndMs);
            Assert.False(hit.Line.Synced);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(500)]
    public async Task LyricsSearch_ExistingTimingWinsAndUnmatchedLinesStayUnsynced(long start)
    {
        var root = Path.Combine(Path.GetTempPath(), "lyrics-line-sync-" + Guid.NewGuid().ToString("N"));
        var context = Context();
        var recording = Guid.NewGuid();
        try
        {
            using var index = new LyricsSearchIndex(new StorageOptions { DataDirectory = root });
            await index.StoreAsync(context, Song(1), recording, new(new("backend", "Library"), "line",
                [new(start, 900, "Existing sunrise", "lead", []), new(null, null, "Unmatched moon", "lead", [])]));
            await index.StoreAsync(context, Song(1), recording, new(new("lrclib", "Timed lyrics"), "line",
                [new(1000, 2000, "Existing sunrise", "lead", []), new(3000, 4000, "Different text", "lead", [])]));
            async Task<LyricsSearchResult> SearchLine(string query) => Assert.Single((await index.SearchAsync(context, query, 20,
                await index.SnapshotAsync(context), Policy(), new Dictionary<Guid, string> { [recording] = "backend" })).Results);
            var existing = await SearchLine("existing sunrise");
            Assert.Equal(start, existing.Line.StartMs);
            Assert.Equal(900, existing.Line.EndMs);
            Assert.True(existing.Line.Synced);
            var unmatched = await SearchLine("unmatched moon");
            Assert.Null(unmatched.Line.StartMs);
            Assert.Null(unmatched.Line.EndMs);
            Assert.False(unmatched.Line.Synced);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task LyricsSearch_StaleVisibilityDoesNotReadReplacedContent()
    {
        var root = Path.Combine(Path.GetTempPath(), "lyrics-replacement-" + Guid.NewGuid().ToString("N"));
        var context = Context();
        try
        {
            using var index = new LyricsSearchIndex(new StorageOptions { DataDirectory = root });
            await index.StoreAsync(context, Song(1), null, Lyrics("Original sunrise"));
            var visible = await index.SnapshotAsync(context);
            await index.StoreAsync(context, Song(1), null, Lyrics("Replacement moon"));
            Assert.Empty((await index.SearchAsync(context, "replacement moon", 20, visible, Policy(), new Dictionary<Guid, string>())).Results);
            Assert.Single((await Search(index, context, "replacement moon")).Results);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task<LyricsSearchResponse> Search(LyricsSearchIndex index, ProtocolExecutionContext context, string query) =>
        await index.SearchAsync(context, query, 20, await index.SnapshotAsync(context), Policy(), new Dictionary<Guid, string>());

    private static EffectiveProviderPolicySnapshot Policy() => new(
        ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty.Add(ProviderCapabilityKind.Lyrics, ["backend"]),
        ImmutableHashSet<string>.Empty, "lossless", 0.07);

    private static ProtocolExecutionContext Context() => new(ProtocolKind.Jellyfin, "primary", "listener",
        new AllstarrPrincipal(Guid.NewGuid(), "jellyfin", "primary", "listener", "Listener", false), "search-fixture",
        DateTimeOffset.UtcNow.AddMinutes(5), default);
    private sealed class SearchClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static Song Song(int id) => new() { Id = $"song-{id}", IsLocal = true, Title = $"Song {id}", Artist = "Artist" };
    private static RichLyrics Lyrics(string text) => new(new("backend", "Library"), "plain",
        text.Split('\n').Select(line => new RichLyricsLine(null, null, line, "lead", [])).ToArray());
}
