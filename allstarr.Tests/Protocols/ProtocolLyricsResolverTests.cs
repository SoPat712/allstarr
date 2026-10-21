using System.Collections.Immutable;
using allstarr.Core.Identity;
using allstarr.Core.Settings;
using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using allstarr.Core.Downloads;
using allstarr.Core.Matching;

namespace allstarr.Tests;

public sealed class ProtocolLyricsResolverTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CrossServiceLyrics_UseOnlyVerifiedResolverResults(bool verified)
    {
        var context = LyricsContext();
        var song = LyricsSong();
        var providers = new Mock<IProtocolProviderGateway>();
        providers.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics)).Returns(["spotify"]);
        providers.Setup(item => item.GetLyricsAsync(context, "spotify", "cross-id", ProviderLyricsFormat.LineTimed,
                song.Title, It.IsAny<IReadOnlyList<string>>(), song.Album, song.Duration, song.Isrc))
            .ReturnsAsync(new ProviderLyricsResult(ProviderLyricsAvailabilityState.Available, "spotify",
                ProviderLyricsFormat.LineTimed, "[00:01.000]Words"));
        var links = new Mock<ICrossProviderLinkResolver>();
        links.Setup(item => item.ResolveAsync(It.IsAny<ExternalTrackMatchSnapshot>(),
                It.IsAny<IReadOnlyList<ProviderExternalResourceId>>(), context.Actor!, context.CorrelationId, context.CancellationToken))
            .ReturnsAsync([new("spotify", "cross-id", verified, verified ? 1 : 0.7, "cross-service-match")]);
        var result = await new ProtocolLyricsResolver(providers.Object, NullLogger<ProtocolLyricsResolver>.Instance, links.Object)
            .FindAsync(context, song, "resource", "deezer", "42");
        Assert.Equal(verified, result != null);
        links.VerifyAll();
        providers.Verify(item => item.GetLyricsAsync(context, "spotify", "cross-id", ProviderLyricsFormat.LineTimed,
            song.Title, It.IsAny<IReadOnlyList<string>>(), song.Album, song.Duration, song.Isrc), verified ? Times.Once() : Times.Never());
    }

    [Theory]
    [InlineData("lrcred")]
    [InlineData("paxsenix")]
    [InlineData("genius")]
    public async Task PublicMetadataLyricsReceiveIsrcAndMetadataFromTheSong(string provider)
    {
        var context = LyricsContext();
        var song = LyricsSong();
        song.Isrc = "USABC1234567";
        var providers = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        providers.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics)).Returns([provider]);
        providers.Setup(item => item.GetLyricsAsync(context, provider, It.IsAny<string>(), ProviderLyricsFormat.LineTimed,
                song.Title, It.IsAny<IReadOnlyList<string>>(), song.Album, song.Duration, song.Isrc))
            .ReturnsAsync(new ProviderLyricsResult(ProviderLyricsAvailabilityState.Available, provider,
                ProviderLyricsFormat.LineTimed, "[00:01.000]Words"));
        var result = await new ProtocolLyricsResolver(providers.Object, NullLogger<ProtocolLyricsResolver>.Instance)
            .FindAsync(context, song, "resource");
        Assert.Equal(provider, result?.Source);
        providers.VerifyAll();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndexedKeptSourceParticipatesLazilyForNativeAndProviderSongs(bool native)
    {
        var context = LyricsContext();
        var song = LyricsSong();
        song.Id = native ? "native-item" : "ext-spotify-song-exact";
        song.IsLocal = native;
        song.ExternalProvider = native ? null : "spotify";
        song.ExternalId = native ? null : "exact";
        var providers = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        providers.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics)).Returns(["kept", "lrclib"]);
        var kept = new Mock<IKeptLyricsReader>(MockBehavior.Strict);
        kept.Setup(item => item.ReadAsync(context, song, It.Is<IReadOnlyList<KeptLyricsIdentity>>(
                ids => ids.Count == 1 && ids[0] == new KeptLyricsIdentity("spotify", "exact"))))
            .ReturnsAsync(new allstarr.Models.Lyrics.LyricsInfo { SyncedLyrics = "[00:01.234]Owned" });
        var index = new Mock<ILibraryIndexService>(MockBehavior.Strict);
        if (native) index.Setup(item => item.FindByBackendItemAsync(context, "native-item", context.CancellationToken))
            .ReturnsAsync(new IndexedLibraryTrack(Guid.CreateVersion7(), "native-item", "/untrusted/never-use.flac",
                "Fixture", "Artist", "Album", null, null, null, null, null, null, null,
                new Dictionary<string, string> { ["spotify"] = "exact" }, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0));
        var result = await new ProtocolLyricsResolver(providers.Object, NullLogger<ProtocolLyricsResolver>.Instance,
                keptLyrics: kept.Object, libraryIndex: index.Object)
            .FindAsync(context, song, song.Id);
        Assert.Equal("kept", result?.Source);
        Assert.Equal("[00:01.234]Owned", result?.SyncedLyrics);
        kept.VerifyAll();
        index.VerifyAll();
        providers.Verify(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics), Times.Once);
        providers.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task OnlySourceReadsJustThatSource()
    {
        var context = LyricsContext();
        var providers = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        providers.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics)).Returns(["lrclib", "backend"]);
        var nativeCalls = 0;
        var candidates = await new ProtocolLyricsResolver(providers.Object, NullLogger<ProtocolLyricsResolver>.Instance)
            .FindAllAsync(context, LyricsSong(), "native-song", new ProtocolLyricsSources(Backend: _ =>
            {
                nativeCalls++;
                return Task.FromResult<allstarr.Models.Lyrics.LyricsInfo?>(new() { SyncedLyrics = "[00:01.000]Native" });
            })
            { OnlySourceId = "backend" });

        var candidate = Assert.Single(candidates);
        Assert.Equal("backend", candidate.SourceId);
        Assert.Equal(1, nativeCalls);
        providers.Verify(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics), Times.Once);
        providers.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EarlierTimedSourceDoesNotReadTheKeptIndexOrFiles()
    {
        var context = LyricsContext();
        var providers = LyricsProviders(context, ProviderLyricsFormat.LineTimed, ProviderLyricsFormat.LineTimed);
        providers.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics)).Returns(["lrclib", "kept"]);
        var kept = new Mock<IKeptLyricsReader>(MockBehavior.Strict);
        var index = new Mock<ILibraryIndexService>(MockBehavior.Strict);
        var result = await new ProtocolLyricsResolver(providers.Object, NullLogger<ProtocolLyricsResolver>.Instance,
            keptLyrics: kept.Object, libraryIndex: index.Object).FindAsync(context, LyricsSong(), "native-item");
        Assert.Equal("lrclib", result?.Source);
        kept.VerifyNoOtherCalls();
        index.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task NativeCallbackCannotJumpAheadOfTheGatewayHouseholdOrderWithoutPersonalPolicy()
    {
        var context = LyricsContext();
        var providers = LyricsProviders(context, ProviderLyricsFormat.LineTimed, ProviderLyricsFormat.LineTimed);
        providers.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics)).Returns(["lrclib", "backend"]);
        var nativeCalls = 0;
        var result = await new ProtocolLyricsResolver(providers.Object, NullLogger<ProtocolLyricsResolver>.Instance)
            .FindAsync(context, LyricsSong(), "native-song", new ProtocolLyricsSources(Backend: _ =>
            {
                nativeCalls++;
                return Task.FromResult<allstarr.Models.Lyrics.LyricsInfo?>(new() { SyncedLyrics = "[00:01.000]Native" });
            }));
        Assert.Equal("lrclib", result?.Source);
        Assert.Equal(0, nativeCalls);
    }

    [Theory]
    [InlineData("backend,kept,lrclib", true, true, "backend")]
    [InlineData("lrclib,backend,kept", true, true, "lrclib")]
    [InlineData("backend,kept,lrclib", false, true, "kept")]
    [InlineData("backend,kept,lrclib", false, false, "backend")]
    public async Task LocalSourcesUseTheListenersOrderAndTheSameTimedPreference(
        string order, bool backendTimed, bool laterTimed, string expected)
    {
        var context = LyricsContext();
        var calls = new List<string>();
        var providers = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        providers.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics)).Returns(["lrclib"]);
        providers.Setup(item => item.GetLyricsAsync(context, "lrclib", It.IsAny<string>(), ProviderLyricsFormat.LineTimed,
                "Fixture", It.IsAny<IReadOnlyList<string>>(), "Album", 120, null))
            .Callback(() => calls.Add("lrclib"))
            .ReturnsAsync(new ProviderLyricsResult(ProviderLyricsAvailabilityState.Available, "lrclib",
                laterTimed ? ProviderLyricsFormat.LineTimed : ProviderLyricsFormat.PlainText,
                laterTimed ? "[00:01.234]Provider" : "Provider"));
        var policies = new Mock<IEffectiveProviderPolicyResolver>(MockBehavior.Strict);
        policies.Setup(item => item.ResolveForUserAsync(context.Actor!.EffectiveUserId!.Value, context.CancellationToken))
            .ReturnsAsync(new EffectiveProviderPolicySnapshot(new Dictionary<ProviderCapabilityKind, ImmutableArray<string>>
            { [ProviderCapabilityKind.Lyrics] = [.. order.Split(',')] }.ToImmutableDictionary(),
                ImmutableHashSet<string>.Empty, "lossless", 0.07));
        Task<allstarr.Models.Lyrics.LyricsInfo?> Read(string source, bool timed, CancellationToken token)
        {
            Assert.Equal(context.CancellationToken, token);
            calls.Add(source);
            return Task.FromResult<allstarr.Models.Lyrics.LyricsInfo?>(new()
            { Source = source, SyncedLyrics = timed ? "[00:01.234]Local" : null, PlainLyrics = timed ? null : "Local" });
        }
        var result = await new ProtocolLyricsResolver(providers.Object, NullLogger<ProtocolLyricsResolver>.Instance,
                effectivePolicies: policies.Object).FindAsync(context, LyricsSong(), "native-song",
            new ProtocolLyricsSources(token => Read("backend", backendTimed, token), token => Read("kept", laterTimed, token)));
        Assert.Equal(expected, result?.Source);
        Assert.Equal(laterTimed || backendTimed, result?.SyncedLyrics != null);
        Assert.Equal(order.Split(',').Take(calls.Count), calls);
        policies.VerifyAll();
    }

    [Fact]
    public async Task DisabledLocalSourcesAreNotReadAndCancellationDuringReplyStopsFallback()
    {
        using var cancellation = new CancellationTokenSource();
        var context = LyricsContext(cancellation.Token);
        var providers = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        providers.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics)).Returns([]);
        var policies = new Mock<IEffectiveProviderPolicyResolver>(MockBehavior.Strict);
        policies.Setup(item => item.ResolveForUserAsync(context.Actor!.EffectiveUserId!.Value, context.CancellationToken))
            .ReturnsAsync(new EffectiveProviderPolicySnapshot(new Dictionary<ProviderCapabilityKind, ImmutableArray<string>>
            { [ProviderCapabilityKind.Lyrics] = ["backend", "kept"] }.ToImmutableDictionary(),
                ImmutableHashSet.Create("backend"), "lossless", 0.07));
        var sources = new ProtocolLyricsSources(_ => throw new InvalidOperationException("Disabled source was read"),
            _ =>
            {
                cancellation.Cancel();
                return Task.FromResult<allstarr.Models.Lyrics.LyricsInfo?>(new() { SyncedLyrics = "[00:01.000]Canceled" });
            });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProtocolLyricsResolver(providers.Object,
            NullLogger<ProtocolLyricsResolver>.Instance, effectivePolicies: policies.Object)
            .FindAsync(context, LyricsSong(), "native-song", sources));
        providers.Verify(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics), Times.Once);
        providers.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("lrclib")]
    [InlineData("lyricsplus")]
    [InlineData("musixmatch")]
    public async Task Resolver_DeezerTrackFallsBackToMetadataLyricsWithFullMetadata(string metadataProvider)
    {
        var protocol = new ProtocolExecutionContext(
            ProtocolKind.Jellyfin,
            "backend",
            "api-key",
            null,
            "lyrics-test",
            DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None);
        var providers = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        providers.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics))
            .Returns(["spotify", metadataProvider]);
        providers.Setup(item => item.GetLyricsAsync(
                protocol,
                metadataProvider,
                It.Is<string>(value => value.Length == 64),
                ProviderLyricsFormat.LineTimed,
                "Fixture song",
                It.Is<IReadOnlyList<string>>(artists => artists.SequenceEqual(
                    new[] { "First artist", "Second artist" })),
                "Fixture album",
                180, null))
            .ReturnsAsync(new ProviderLyricsResult(
                ProviderLyricsAvailabilityState.Available,
                metadataProvider,
                ProviderLyricsFormat.LineTimed,
                "[00:01.00]Fixture line\n",
                "sha256:fixture"));
        var resolver = new ProtocolLyricsResolver(
            providers.Object,
            NullLogger<ProtocolLyricsResolver>.Instance);

        var result = await resolver.FindAsync(protocol, new Song
        {
            Title = "Fixture song",
            Artist = "First artist",
            Artists = ["First artist", "Second artist"],
            Album = "Fixture album",
            Duration = 180
        }, "ext-deezer-song-3135556", "deezer", "3135556");

        Assert.NotNull(result);
        Assert.Equal("[00:01.00]Fixture line\n", result.SyncedLyrics);
        Assert.Null(result.PlainLyrics);
        Assert.Equal(metadataProvider, result.Source);
        Assert.Equal("sha256:fixture", result.Revision);
        providers.Verify(item => item.GetLyricsAsync(
            It.IsAny<ProtocolExecutionContext>(), "spotify", It.IsAny<string>(),
            It.IsAny<ProviderLyricsFormat?>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<string>(), It.IsAny<int?>(), null), Times.Never);
    }

    [Fact]
    public async Task Resolver_HonorsConfiguredOrderBeforeCatalogSource()
    {
        var protocol = new ProtocolExecutionContext(
            ProtocolKind.Jellyfin, "backend", "api-key", null, "lyrics-source-first",
            DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
        var providers = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        providers.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics))
            .Returns(["lrclib", "spotiflac-apple-music"]);
        providers.Setup(item => item.GetLyricsAsync(
                protocol, "lrclib", It.IsAny<string>(), ProviderLyricsFormat.LineTimed,
                "Fixture song", It.IsAny<IReadOnlyList<string>>(), "Fixture album", 240, null))
            .ReturnsAsync(new ProviderLyricsResult(
                ProviderLyricsAvailabilityState.Available, "lrclib",
                ProviderLyricsFormat.LineTimed, "[00:01.00]Fixture line\n"));
        var resolver = new ProtocolLyricsResolver(
            providers.Object, NullLogger<ProtocolLyricsResolver>.Instance);

        var result = await resolver.FindAsync(
            protocol,
            new Song { Title = "Fixture song", Artist = "Artist", Album = "Fixture album", Duration = 240 },
            "library-song-id",
            "spotiflac-apple-music",
            "apple-track");

        Assert.Equal("lrclib", result?.Source);
        providers.Verify(item => item.GetLyricsAsync(
            It.IsAny<ProtocolExecutionContext>(), "spotiflac-apple-music", It.IsAny<string>(),
            It.IsAny<ProviderLyricsFormat?>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<string>(), It.IsAny<int?>(), null), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Resolver_UsesResolvedCrosslinksBeforeLyricsFallback(bool cached)
    {
        var protocol = new ProtocolExecutionContext(
            ProtocolKind.Jellyfin, "backend", "listener", new AllstarrPrincipal(Guid.NewGuid(), "jellyfin", "backend", "listener", "Listener", false), "lyrics-translation",
            DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
        var providers = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        providers.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics))
            .Returns(["spotify", "apple-download", "lrclib"]);
        providers.Setup(item => item.GetLyricsAsync(
                protocol, "spotify", "2takcwOaAZWiXQijPHIx7B", ProviderLyricsFormat.LineTimed,
                "Rocket", It.IsAny<IReadOnlyList<string>>(), "Fixture album", 210, null))
            .ReturnsAsync((ProviderLyricsResult?)null);
        providers.Setup(item => item.GetLyricsAsync(
                protocol, "apple-download", "2037093408", ProviderLyricsFormat.LineTimed,
                "Rocket", It.IsAny<IReadOnlyList<string>>(), "Fixture album", 210, null))
            .ReturnsAsync(new ProviderLyricsResult(
                ProviderLyricsAvailabilityState.Available, "apple-download",
                ProviderLyricsFormat.LineTimed, "[00:01.00]Rocket line\n"));
        providers.Setup(item => item.GetLyricsAsync(protocol, "lrclib", It.IsAny<string>(),
                ProviderLyricsFormat.LineTimed, "Rocket", It.IsAny<IReadOnlyList<string>>(), "Fixture album", 210, null))
            .ReturnsAsync(new ProviderLyricsResult(ProviderLyricsAvailabilityState.Available, "lrclib",
                ProviderLyricsFormat.LineTimed, "[00:01.00]Rocket line\n"));
        var links = new Mock<ICrossProviderLinkResolver>();
        links.Setup(item => item.ResolveAsync(It.IsAny<ExternalTrackMatchSnapshot>(),
                It.IsAny<IReadOnlyList<ProviderExternalResourceId>>(), It.IsAny<ProviderActorContext>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(cached
                ? [new("spotify", "2takcwOaAZWiXQijPHIx7B", true, 1, "isrc-lookup"),
                    new("apple-musickit", "2037093408", true, 1, "isrc-lookup")]
                : []);
        var resolver = new ProtocolLyricsResolver(
            providers.Object, NullLogger<ProtocolLyricsResolver>.Instance, links.Object);

        var result = await resolver.FindAsync(
            protocol,
            new Song { Title = "Rocket", Artist = "Artist", Album = "Fixture album", Duration = 210 },
            "ext-deezer-song-13190193",
            "deezer",
            "13190193");

        if (!cached)
        {
            Assert.Equal("lrclib", result?.Source);
            Assert.DoesNotContain(providers.Invocations, call => call.Method.Name == nameof(IProtocolProviderGateway.GetLyricsAsync) &&
                call.Arguments[1] is "spotify" or "apple-download");
            return;
        }
        Assert.Equal(
            "spotify:2takcwOaAZWiXQijPHIx7B|apple-download:2037093408",
            string.Join('|', providers.Invocations
                .Where(call => call.Method.Name == nameof(IProtocolProviderGateway.GetLyricsAsync))
                .Select(call => $"{call.Arguments[1]}:{call.Arguments[2]}")));
        Assert.Equal("apple-download", result?.Source);
        Assert.Equal("[00:01.00]Rocket line\n", result?.SyncedLyrics);
        providers.Verify(item => item.GetLyricsAsync(
            It.IsAny<ProtocolExecutionContext>(), "lrclib", It.IsAny<string>(),
            It.IsAny<ProviderLyricsFormat?>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<string>(), It.IsAny<int?>(), null), Times.Never);
    }


    [Theory]
    [InlineData(ProviderLyricsFormat.PlainText, ProviderLyricsFormat.LineTimed, "lrclib")]
    [InlineData(ProviderLyricsFormat.PlainText, ProviderLyricsFormat.PlainText, "spotify")]
    [InlineData(ProviderLyricsFormat.LineTimed, ProviderLyricsFormat.LineTimed, "spotify")]
    public async Task Resolver_PrefersTimedContentThenConfiguredOrder(
        ProviderLyricsFormat first, ProviderLyricsFormat second, string expected)
    {
        var context = LyricsContext();
        var providers = LyricsProviders(context, first, second);
        var result = await new ProtocolLyricsResolver(providers.Object, NullLogger<ProtocolLyricsResolver>.Instance)
            .FindAsync(context, LyricsSong(), "recording", "spotify", "fixture-track");
        Assert.Equal(expected, result?.Source);
        Assert.Equal(first == ProviderLyricsFormat.PlainText && second == ProviderLyricsFormat.PlainText,
            result?.SyncedLyrics == null);
        providers.Verify(item => item.GetLyricsAsync(context, "spotify", "fixture-track",
            ProviderLyricsFormat.LineTimed, "Fixture", It.IsAny<IReadOnlyList<string>>(), "Album", 120, null), Times.Once);
        providers.Verify(item => item.GetLyricsAsync(context, "lrclib", It.IsAny<string>(),
            ProviderLyricsFormat.LineTimed, "Fixture", It.IsAny<IReadOnlyList<string>>(), "Album", 120, null),
            first == ProviderLyricsFormat.LineTimed ? Times.Never() : Times.Once());
    }

    [Fact]
    public async Task Resolver_UsesTheExactListenersOrderAndDisabledSources()
    {
        var context = LyricsContext();
        var providers = LyricsProviders(context, ProviderLyricsFormat.LineTimed, ProviderLyricsFormat.LineTimed);
        var policies = new Mock<IEffectiveProviderPolicyResolver>(MockBehavior.Strict);
        policies.Setup(item => item.ResolveForUserAsync(context.Actor!.EffectiveUserId!.Value, context.CancellationToken))
            .ReturnsAsync(new EffectiveProviderPolicySnapshot(
                new Dictionary<ProviderCapabilityKind, ImmutableArray<string>>
                { [ProviderCapabilityKind.Lyrics] = ["lrclib", "spotify"] }.ToImmutableDictionary(),
                ImmutableHashSet.Create("spotify"), "lossless", 0.07));
        var resolver = new ProtocolLyricsResolver(providers.Object, NullLogger<ProtocolLyricsResolver>.Instance,
            effectivePolicies: policies.Object);
        var result = await resolver.FindAsync(context, LyricsSong(), "recording", "spotify", "fixture-track");
        Assert.Equal("lrclib", result?.Source);
        providers.Verify(item => item.GetLyricsAsync(context, "spotify", It.IsAny<string>(),
            It.IsAny<ProviderLyricsFormat?>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<string>(), It.IsAny<int?>(), null), Times.Never);
        policies.VerifyAll();
    }

    [Fact]
    public async Task Resolver_FailureFallsBackButCancellationStops()
    {
        using var canceled = new CancellationTokenSource();
        var context = LyricsContext(canceled.Token);
        var providers = LyricsProviders(context, ProviderLyricsFormat.PlainText, ProviderLyricsFormat.LineTimed);
        providers.Setup(item => item.GetLyricsAsync(context, "spotify", "fixture-track",
                ProviderLyricsFormat.LineTimed, "Fixture", It.IsAny<IReadOnlyList<string>>(), "Album", 120, null))
            .ThrowsAsync(new HttpRequestException("Private upstream detail"));
        var resolver = new ProtocolLyricsResolver(providers.Object, NullLogger<ProtocolLyricsResolver>.Instance);
        Assert.Equal("lrclib", (await resolver.FindAsync(context, LyricsSong(), "recording", "spotify", "fixture-track"))?.Source);
        providers.Invocations.Clear();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            resolver.FindAsync(context, LyricsSong(), "recording", "spotify", "fixture-track"));
        providers.Verify(item => item.GetLyricsAsync(context, It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<ProviderLyricsFormat?>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<string>(), It.IsAny<int?>(), null), Times.Never);
    }

    [Fact]
    public async Task Resolver_CancellationDuringProviderReplyCannotReturnLyrics()
    {
        using var canceled = new CancellationTokenSource();
        var context = LyricsContext(canceled.Token);
        var providers = LyricsProviders(context, ProviderLyricsFormat.LineTimed, ProviderLyricsFormat.LineTimed);
        providers.Setup(item => item.GetLyricsAsync(context, "spotify", "fixture-track",
                ProviderLyricsFormat.LineTimed, "Fixture", It.IsAny<IReadOnlyList<string>>(), "Album", 120, null))
            .Callback(canceled.Cancel)
            .ReturnsAsync(new ProviderLyricsResult(ProviderLyricsAvailabilityState.Available, "spotify",
                ProviderLyricsFormat.LineTimed, "[00:01.00]Synthetic words"));
        var resolver = new ProtocolLyricsResolver(providers.Object, NullLogger<ProtocolLyricsResolver>.Instance);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            resolver.FindAsync(context, LyricsSong(), "recording", "spotify", "fixture-track"));
        providers.Verify(item => item.GetLyricsAsync(context, "lrclib", It.IsAny<string>(),
            It.IsAny<ProviderLyricsFormat?>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<string>(), It.IsAny<int?>(), null), Times.Never);
    }

    [Theory]
    [InlineData(ProviderLyricsFormat.PlainText)]
    [InlineData(ProviderLyricsFormat.LineTimed)]
    public async Task EarlierLyricsReturnWithoutWaitingForPaxsenix(ProviderLyricsFormat format)
    {
        var context = LyricsContext();
        var providers = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        providers.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics)).Returns(["lrclib", "paxsenix"]);
        providers.Setup(item => item.GetLyricsAsync(context, "lrclib", It.IsAny<string>(), ProviderLyricsFormat.LineTimed,
                "Fixture", It.IsAny<IReadOnlyList<string>>(), "Album", 120, null))
            .ReturnsAsync(new ProviderLyricsResult(ProviderLyricsAvailabilityState.Available, "lrclib", format,
                format == ProviderLyricsFormat.PlainText ? "Words" : "[00:01]Words"));
        providers.Setup(item => item.GetLyricsAsync(context, "paxsenix", It.IsAny<string>(), ProviderLyricsFormat.LineTimed,
                "Fixture", It.IsAny<IReadOnlyList<string>>(), "Album", 120, null))
            .Returns(new TaskCompletionSource<ProviderLyricsResult?>().Task);

        var pending = new ProtocolLyricsResolver(providers.Object, NullLogger<ProtocolLyricsResolver>.Instance)
            .FindAsync(context, LyricsSong(), "recording");

        Assert.True(pending.IsCompletedSuccessfully);
        Assert.Equal("lrclib", (await pending)?.Source);
        providers.Verify(item => item.GetLyricsAsync(context, "paxsenix", It.IsAny<string>(), ProviderLyricsFormat.LineTimed,
            "Fixture", It.IsAny<IReadOnlyList<string>>(), "Album", 120, null), Times.Never);
    }

    [Fact]
    public async Task PaxsenixTimeoutFallsThroughToTheNextConfiguredSource()
    {
        var context = LyricsContext();
        var providers = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        providers.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics)).Returns(["paxsenix", "lrclib"]);
        providers.Setup(item => item.GetLyricsAsync(context, "paxsenix", It.IsAny<string>(), ProviderLyricsFormat.LineTimed,
                "Fixture", It.IsAny<IReadOnlyList<string>>(), "Album", 120, null))
            .ThrowsAsync(new HttpRequestException("The lyrics upstream is busy or timed out."));
        providers.Setup(item => item.GetLyricsAsync(context, "lrclib", It.IsAny<string>(), ProviderLyricsFormat.LineTimed,
                "Fixture", It.IsAny<IReadOnlyList<string>>(), "Album", 120, null))
            .ReturnsAsync(new ProviderLyricsResult(ProviderLyricsAvailabilityState.Available, "lrclib",
                ProviderLyricsFormat.LineTimed, "[00:01]Words"));

        var result = await new ProtocolLyricsResolver(providers.Object, NullLogger<ProtocolLyricsResolver>.Instance)
            .FindAsync(context, LyricsSong(), "recording");

        Assert.Equal("lrclib", result?.Source);
        providers.VerifyAll();
    }

    private static ProtocolExecutionContext LyricsContext(CancellationToken cancellationToken = default) => new(
        ProtocolKind.Jellyfin, "backend", "listener", new AllstarrPrincipal(Guid.NewGuid(), "jellyfin", "backend",
            "listener", "Listener", false), "lyrics-order", DateTimeOffset.UtcNow.AddMinutes(1), cancellationToken);
    private static Song LyricsSong() => new()
    { Title = "Fixture", Artist = "Artist", Artists = ["Artist"], Album = "Album", Duration = 120 };
    private static Mock<IProtocolProviderGateway> LyricsProviders(ProtocolExecutionContext context,
        ProviderLyricsFormat first, ProviderLyricsFormat second)
    {
        var providers = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        providers.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics)).Returns(["spotify", "lrclib"]);
        foreach (var (id, format) in new[] { ("spotify", first), ("lrclib", second) })
            providers.Setup(item => item.GetLyricsAsync(context, id, It.IsAny<string>(), ProviderLyricsFormat.LineTimed,
                    "Fixture", It.IsAny<IReadOnlyList<string>>(), "Album", 120, null))
                .ReturnsAsync(new ProviderLyricsResult(ProviderLyricsAvailabilityState.Available, id, format,
                    format == ProviderLyricsFormat.PlainText ? "Synthetic words" : "[00:01.00]Synthetic words"));
        return providers;
    }

}
