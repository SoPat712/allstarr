using System.Collections.Immutable;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Models.Domain;
using allstarr.Models.Lyrics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed class ProtocolLyricsPreviewTests
{
    [Fact]
    public async Task PreviewReadsEverySourceEvenAfterTimedContentAndKeepsUnavailableRows()
    {
        var context = Context();
        var calls = new List<string>();
        var providers = Providers(context, calls);
        var source = new Song
        {
            Id = "ext-spotify-song-exact",
            Title = "Fixture",
            Artist = "Artist",
            ExternalProvider = "spotify",
            ExternalId = "exact"
        };
        var results = await Resolver(providers).FindAllAsync(context, source, source.Id,
            new ProtocolLyricsSources(_ =>
            {
                calls.Add("backend");
                return Task.FromResult<LyricsInfo?>(new() { PlainLyrics = "Native plain words" });
            }));
        Assert.Equal(["spotify", "backend", "lrclib", "lyricsplus"], results.Select(item => item.SourceId));
        Assert.Equal(results.Select(item => item.SourceId), calls);
        Assert.Equal("[00:01.234]Spotify words", results[0].Lyrics?.SyncedLyrics);
        Assert.Equal("backend", results[1].Lyrics?.Source);
        Assert.Equal("Native plain words", results[1].Lyrics?.PlainLyrics);
        Assert.Null(results[2].Lyrics);
        Assert.Equal("[00:02.000]LyricsPlus words", results[3].Lyrics?.SyncedLyrics);
    }

    [Fact]
    public async Task PreviewUsesPersonalOrderAndNeverReadsDisabledSources()
    {
        var context = Context();
        var calls = new List<string>();
        var providers = Providers(context, calls);
        var policy = new Mock<IEffectiveProviderPolicyResolver>(MockBehavior.Strict);
        policy.Setup(item => item.ResolveForUserAsync(context.Principal!.UserId, context.CancellationToken))
            .ReturnsAsync(new EffectiveProviderPolicySnapshot(
                new Dictionary<ProviderCapabilityKind, ImmutableArray<string>>
                { [ProviderCapabilityKind.Lyrics] = ["lyricsplus", "spotify", "backend", "lrclib"] }.ToImmutableDictionary(),
                ImmutableHashSet.Create("backend", "lrclib"), "lossless", .07));
        var results = await new ProtocolLyricsResolver(providers.Object, NullLogger<ProtocolLyricsResolver>.Instance,
            effectivePolicies: policy.Object).FindAllAsync(context,
            new Song { Title = "Fixture", Artist = "Artist", ExternalProvider = "spotify", ExternalId = "exact" },
            "recording", new ProtocolLyricsSources(_ => throw new InvalidOperationException("Disabled backend was read")));
        Assert.Equal(["lyricsplus", "spotify"], results.Select(item => item.SourceId));
        Assert.Equal(["lyricsplus", "spotify"], calls);
    }

    [Fact]
    public async Task PreviewPreservesCancellationDuringAReply()
    {
        using var cancellation = new CancellationTokenSource();
        var context = Context(cancellation.Token);
        var calls = new List<string>();
        var providers = Providers(context, calls);
        providers.Setup(item => item.GetLyricsAsync(context, "spotify", "exact", ProviderLyricsFormat.LineTimed,
                "Fixture", It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<int?>(), null))
            .Callback(cancellation.Cancel).ReturnsAsync(new ProviderLyricsResult(ProviderLyricsAvailabilityState.Available,
                "spotify", ProviderLyricsFormat.LineTimed, "[00:01.234]Canceled words"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Resolver(providers).FindAllAsync(context,
            new Song { Title = "Fixture", Artist = "Artist", ExternalProvider = "spotify", ExternalId = "exact" }, "recording"));
        providers.Verify(item => item.GetLyricsAsync(context, "lyricsplus", It.IsAny<string>(), It.IsAny<ProviderLyricsFormat?>(),
            It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<int?>(), null), Times.Never);
    }

    private static ProtocolLyricsResolver Resolver(Mock<IProtocolProviderGateway> providers) =>
        new(providers.Object, NullLogger<ProtocolLyricsResolver>.Instance);

    private static Mock<IProtocolProviderGateway> Providers(ProtocolExecutionContext context, List<string> calls)
    {
        var providers = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        providers.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics)).Returns(["spotify", "backend", "lrclib", "lyricsplus"]);
        foreach (var id in new[] { "spotify", "lrclib", "lyricsplus" })
        {
            var source = id;
            var setup = providers.Setup(item => item.GetLyricsAsync(context, source, It.IsAny<string>(),
                ProviderLyricsFormat.LineTimed, "Fixture", It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<int?>(), null))
                .Callback(() => calls.Add(source));
            if (id == "lrclib") setup.ThrowsAsync(new HttpRequestException("Synthetic unavailable source"));
            else setup.ReturnsAsync(new ProviderLyricsResult(ProviderLyricsAvailabilityState.Available, source,
                ProviderLyricsFormat.LineTimed, id == "spotify" ? "[00:01.234]Spotify words" : "[00:02.000]LyricsPlus words"));
        }
        return providers;
    }

    private static ProtocolExecutionContext Context(CancellationToken token = default) => new(ProtocolKind.Jellyfin,
        "backend", "listener", new AllstarrPrincipal(Guid.CreateVersion7(), "jellyfin", "backend", "listener", "Listener", false),
        "preview", DateTimeOffset.UtcNow.AddMinutes(1), token);
}
