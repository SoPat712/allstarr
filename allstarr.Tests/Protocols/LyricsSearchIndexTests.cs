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
