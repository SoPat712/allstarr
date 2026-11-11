using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Models.Search;
using allstarr.Models.Subsonic;
using allstarr.Services.Common;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolProviderStreamingGatewayTests
{
    [Fact]
    public async Task SearchStreamRanksBeforeCappingEachMetadataKindAndDropsLooseArtists()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var provider = DelayedSearchProvider("deezer", clock, TimeSpan.Zero);
        provider.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("deezer",
                [new(new("deezer", ProviderResourceKind.Track, "loose"), "Other", [new("Other")]),
                 new(new("deezer", ProviderResourceKind.Track, "brick"), "Brick By Brick", [new("Say Now")], duration: TimeSpan.FromSeconds(200)),
                 new(new("deezer", ProviderResourceKind.Track, "supermarket"), "Supermarket", [new("Say Now")], duration: TimeSpan.FromSeconds(200)),
                 new(new("deezer", ProviderResourceKind.Track, "match"), "Met my match", [new("Say Now")], duration: TimeSpan.FromSeconds(200))])));
        provider.Setup(item => item.SearchAlbumsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>.Success(new("deezer",
                [new(new("deezer", ProviderResourceKind.Album, "loose"), "Other", [new("Other")]),
                 new(new("deezer", ProviderResourceKind.Album, "release"), "Release", [new("Say Now")]),
                 new(new("deezer", ProviderResourceKind.Album, "exact"), "Say Now", [new("Say Now")]),
                 new(new("deezer", ProviderResourceKind.Album, "another"), "Another", [new("Say Now")])])));
        provider.Setup(item => item.SearchArtistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderArtistMetadata>>.Success(new("deezer",
                [new(new("deezer", ProviderResourceKind.Artist, "loose"), "Now"),
                 new(new("deezer", ProviderResourceKind.Artist, "exact"), "Say Now"),
                 new(new("deezer", ProviderResourceKind.Artist, "prefix"), "Say Now Club"),
                 new(new("deezer", ProviderResourceKind.Artist, "tokens"), "Now Say"),
                 new(new("deezer", ProviderResourceKind.Artist, "partial"), "Say When")])));
        SearchResult? rows = null;
        await BudgetGateway(clock, new TestMemoryApplicationCache(), provider.Object).StreamSearchAsync(
            Context(), "say now", 3, true, true, true, false, (_, result, _, _) =>
            { rows = result; return Task.CompletedTask; });
        Assert.NotNull(rows);
        Assert.Equal(["Brick By Brick", "Supermarket", "Met my match"], rows.Songs.Select(song => song.Title));
        Assert.Equal(["Say Now", "Release", "Another"], rows.Albums.Select(album => album.Title));
        Assert.Equal(["Say Now", "Now Say", "Say Now Club"], rows.Artists.Select(artist => artist.Name));
        Assert.Equal((3, 3, 3), (rows.Songs.Count, rows.Albums.Count, rows.Artists.Count));
        searchOutput.WriteLine("Stream fixture counts (before -> emitted): songs 4 -> 3; albums 4 -> 3; artists 5 -> 3.");
        foreach (var song in rows.Songs)
            searchOutput.WriteLine($"Stream fixture song: {song.Title}; score={SearchRelevanceRanker.ScoreSong("say now", song)}");
        foreach (var album in rows.Albums)
            searchOutput.WriteLine($"Stream fixture album: {album.Title}; score={SearchRelevanceRanker.ScoreAlbum("say now", album)}");
        foreach (var name in new[] { "Say Now", "Say Now Club", "Now Say", "Now", "Say When" })
            searchOutput.WriteLine($"Stream fixture artist: {name}; score={SearchRelevanceRanker.Score("say now", name)}; emitted={rows.Artists.Any(artist => artist.Name == name)}");
    }

    [Fact]
    public async Task SearchStreamKeepsOnlyTheBestArtistWhenEveryArtistIsBelowTheTokenBand()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var provider = DelayedSearchProvider("deezer", clock, TimeSpan.Zero);
        provider.Setup(item => item.SearchArtistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderArtistMetadata>>.Success(new("deezer",
                [new(new("deezer", ProviderResourceKind.Artist, "other"), "Other"),
                 new(new("deezer", ProviderResourceKind.Artist, "closest"), "Say"),
                 new(new("deezer", ProviderResourceKind.Artist, "loose"), "Say When")])));
        SearchResult? rows = null;
        await BudgetGateway(clock, new TestMemoryApplicationCache(), provider.Object).StreamSearchAsync(
            Context(), "say now", 20, false, false, true, false, (_, result, _, _) =>
            { rows = result; return Task.CompletedTask; });
        Assert.Equal("Say", Assert.Single(rows!.Artists).Name);
    }

    [Fact]
    public async Task SearchStreamRanksAndCapsPlaylistsWhenTheProviderReturnsTooMany()
    {
        var capability = new Mock<IProviderPlaylistCapability>(MockBehavior.Strict);
        capability.SetupGet(item => item.ProviderId).Returns("spotify");
        capability.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Playlist);
        capability.Setup(item => item.SearchPlaylistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderPlaylistSearchRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Success(new("spotify",
                new[] { "Other", "Say Now", "Say Now Club", "Say" }.Select((name, index) => new ProviderPlaylistSummary(
                    new("spotify", ProviderResourceKind.Playlist, index.ToString()), name, new("owner", "Curator"), "revision")))));
        var descriptor = new ProviderDescriptor("spotify", "Spotify", "Playlist fixture", ProviderOrigin.BuiltIn, "1", "1",
            [new(ProviderCapabilityKind.Playlist, ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.Required,
                "1", ["getUserPlaylists", "getPlaylistTracks", "searchPlaylists"], [ProviderAccountScope.Shared])], new());
        var registry = new ProviderRegistry([new(descriptor, [capability.Object])]);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderPlaylistCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => new ProviderRoutePlan<IProviderPlaylistCapability>(request,
                [new(0, descriptor, descriptor.Capabilities.Single(), capability.Object,
                    new(request.Actor, descriptor.Id, new(Guid.NewGuid(), "spotify", ProviderAccountScope.Shared, 1), request.Policy, request.OperationId, request.CorrelationId,
                        request.Deadline, request.CancellationToken), null)],
                new(request.CorrelationId, ProviderCapabilityKind.Playlist, descriptor.Id, null, [])));
        var gateway = new ProtocolProviderGateway(router.Object, registry, new HttpClientFactory());
        IReadOnlyList<ExternalPlaylist>? rows = null;
        await gateway.StreamSearchAsync(Context(), "say now", 2, false, false, false, true, (_, _, playlists, _) =>
        { rows = playlists; return Task.CompletedTask; });
        Assert.Equal(["Say Now", "Say Now Club"], rows!.Select(playlist => playlist.Name));
    }

    [Fact]
    public async Task SearchStreamDisconnectKeepsTheMergedSearchSharedFetchAlive()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var provider = DelayedSearchProvider("deezer", clock, TimeSpan.Zero);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ProviderOutcome<ProviderPage<ProviderTrackMetadata>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken fetchToken = default;
        provider.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .Returns((ProviderExecutionContext execution, ProviderMetadataSearchRequest _) =>
            {
                fetchToken = execution.CancellationToken;
                entered.TrySetResult();
                return release.Task;
            });
        var gateway = BudgetGateway(clock, new TestMemoryApplicationCache(), provider.Object);
        var context = Context();
        var merged = gateway.SearchAsync(context, "a", 10, 0, 0);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var stop = new CancellationTokenSource();
        var stream = gateway.StreamSearchAsync(context, "a", 10, true, false, false, false,
            (_, _, _, _) => Task.CompletedTask, stop.Token);
        stop.Cancel();
        Assert.Equal(["deezer"], await stream.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(fetchToken.IsCancellationRequested);
        release.SetResult(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("deezer",
            [new(new("deezer", ProviderResourceKind.Track, "shared"), "Track", [new("Artist")])])));
        Assert.Single((await merged.WaitAsync(TimeSpan.FromSeconds(5))).Songs);
        VerifySearchCount(provider, "songs", 1);
    }

    [Fact]
    public async Task SearchStreamEmitsFastProviderBeforeSlowProviderCompletes()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var fast = DelayedSearchProvider("deezer", clock, TimeSpan.Zero);
        var slow = DelayedSearchProvider("qobuz", clock, TimeSpan.Zero);
        var release = new TaskCompletionSource<ProviderOutcome<ProviderPage<ProviderTrackMetadata>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        slow.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .Returns(release.Task);
        var emitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batches = new List<string>();
        var stream = BudgetGateway(clock, new TestMemoryApplicationCache(), fast.Object, slow.Object)
            .StreamSearchAsync(Context(), "a", 10, true, false, false, false, (provider, _, _, _) =>
            { batches.Add(provider); emitted.TrySetResult(); return Task.CompletedTask; });
        await emitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["deezer"], batches);
        Assert.False(stream.IsCompleted);
        release.SetResult(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("qobuz", [])));
        Assert.Empty(await stream);
        Assert.Equal(["deezer", "qobuz"], batches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SearchStreamCancelsPendingProviderAndReportsItLate(bool deadline)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var provider = DelayedSearchProvider("deezer", clock, TimeSpan.Zero);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .Returns(async (ProviderExecutionContext context, ProviderMetadataSearchRequest _) =>
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken); }
                catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
                return ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("deezer", []));
            });
        using var stop = new CancellationTokenSource();
        var stream = BudgetGateway(clock, new TestMemoryApplicationCache(), provider.Object)
            .StreamSearchAsync(Context(), "a", 10, true, false, false, false, (_, _, _, _) => Task.CompletedTask, stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (deadline) clock.Advance(TimeSpan.FromSeconds(10));
        else stop.Cancel();
        Assert.Equal(["deezer"], await stream.WaitAsync(TimeSpan.FromSeconds(5)));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
