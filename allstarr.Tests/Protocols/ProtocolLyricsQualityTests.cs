using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Core.Protocols.Jellyfin;
using allstarr.Core.Protocols.Subsonic;
using allstarr.Models.Domain;
using allstarr.Models.Lyrics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed class ProtocolLyricsQualityTests
{
    [Theory]
    [InlineData("line", "word", "backend,lyricsplus", "lyricsplus", 1)]
    [InlineData("plain", "line", "backend,lyricsplus", "lyricsplus", 1)]
    [InlineData("word", "word", "backend,lyricsplus", "backend", 0)]
    [InlineData("line", "line", "backend,lyricsplus", "backend", 1)]
    [InlineData("line", "line", "lyricsplus,backend", "lyricsplus", 1)]
    [InlineData("word", "word", "lyricsplus,backend", "lyricsplus", 1)]
    [InlineData("plain", "plain", "lyricsplus,backend", "lyricsplus", 1)]
    public async Task LocalAndServiceLyricsUseQualityThenConfiguredOrder(string localFormat, string serviceFormat,
        string order, string expected, int serviceCalls)
    {
        var context = Context();
        var providers = Providers(context, order.Split(','), "lyricsplus", serviceFormat);
        var resolver = new ProtocolLyricsResolver(providers.Object, NullLogger<ProtocolLyricsResolver>.Instance);
        var result = await resolver.FindAsync(context, Song(), "recording", new ProtocolLyricsSources(
            Backend: _ => Task.FromResult<LyricsInfo?>(Lyrics(localFormat))));
        Assert.Equal(expected, result?.Source);
        Assert.Equal(expected == "backend" ? localFormat : serviceFormat, RichLyricsMapper.Map(result)?.Format);
        Assert.Equal(serviceCalls, providers.Invocations.Count(call => call.Method.Name == nameof(IProtocolProviderGateway.GetLyricsAsync)));
    }

    [Fact]
    public async Task EarlierProviderWinsARealWordTimingTieWithLocalLyrics()
    {
        var context = Context();
        var providers = Providers(context, ["lrclib", "kept"], "lrclib", "word");
        var resolver = new ProtocolLyricsResolver(providers.Object, NullLogger<ProtocolLyricsResolver>.Instance);
        var sources = new ProtocolLyricsSources(Kept: _ => Task.FromResult<LyricsInfo?>(Lyrics("word")));
        var song = Song();
        var candidates = await resolver.FindAllAsync(context, song, "recording", sources);
        var best = candidates.OrderByDescending(candidate => candidate.Assessment?.Score).First();
        var selected = await resolver.FindAsync(context, song, "recording", sources);
        Assert.Equal("lrclib", best.SourceId);
        Assert.Equal(best.SourceId, selected?.Source);
    }

    [Theory]
    [InlineData("backend")]
    [InlineData("kept")]
    [InlineData("lyricsplus")]
    public async Task PinnedSourceWinsWithoutReadingOtherSources(string pin)
    {
        var context = Context();
        var song = Song();
        var recording = song.CanonicalRecordingId!.Value;
        var choices = new Mock<IRecordingLyricsChoices>(MockBehavior.Strict);
        choices.Setup(item => item.GetAsync(context, recording)).ReturnsAsync(new RecordingLyricsChoiceLayers(
            new(Guid.CreateVersion7(), recording, context.Principal!.UserId, pin, 1), null));
        var providers = Providers(context, ["backend", "kept", "lyricsplus"], "lyricsplus", "plain");
        var localCalls = new List<string>();
        Task<LyricsInfo?> Read(string id)
        {
            localCalls.Add(id);
            return Task.FromResult<LyricsInfo?>(Lyrics("plain"));
        }
        var resolver = new ProtocolLyricsResolver(providers.Object, NullLogger<ProtocolLyricsResolver>.Instance, choices: choices.Object);
        var result = await resolver.FindAsync(context, song, "recording", new ProtocolLyricsSources(
            Backend: _ => Read("backend"), Kept: _ => Read("kept")));
        Assert.Equal(pin, result?.Source);
        Assert.Equal(pin == "lyricsplus" ? Array.Empty<string>() : [pin], localCalls);
        Assert.Equal(pin == "lyricsplus" ? 1 : 0,
            providers.Invocations.Count(call => call.Method.Name == nameof(IProtocolProviderGateway.GetLyricsAsync)));
    }

    [Fact]
    public async Task LocalLineSkipsPlainSourcesAndStopsAtFirstWordResult()
    {
        var context = Context();
        var providers = Providers(context, ["backend", "genius", "spotify", "lrclib", "lyricsplus", "musixmatch"], "lyricsplus", "word");
        providers.Setup(item => item.GetLyricsAsync(context, "lrclib", It.IsAny<string>(), ProviderLyricsFormat.LineTimed,
            "Fixture", It.IsAny<IReadOnlyList<string>>(), "Album", 120, null)).ReturnsAsync((ProviderLyricsResult?)null);
        var result = await new ProtocolLyricsResolver(providers.Object, NullLogger<ProtocolLyricsResolver>.Instance)
            .FindAsync(context, Song(), "recording", new ProtocolLyricsSources(Backend: _ => Task.FromResult<LyricsInfo?>(Lyrics("line"))));
        Assert.Equal("lyricsplus", result?.Source);
        Assert.Equal(new[] { "lrclib", "lyricsplus" }, providers.Invocations.Where(call =>
            call.Method.Name == nameof(IProtocolProviderGateway.GetLyricsAsync)).Select(call => call.Arguments[1]));
    }

    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task BetterServiceWordsFeedRichLyricsAndExistingProtocolLines(ProtocolKind kind)
    {
        var context = Context(kind);
        var providers = Providers(context, ["backend", "lyricsplus"], "lyricsplus", "word");
        var resolver = new ProtocolLyricsResolver(providers.Object, NullLogger<ProtocolLyricsResolver>.Instance);
        var sources = new ProtocolLyricsSources(Backend: _ => Task.FromResult<LyricsInfo?>(Lyrics("line")));
        var song = Song();
        var selected = await resolver.FindAsync(context, song, "recording", sources);
        var rich = await resolver.GetRichLyricsAsync(context, song, "recording", sources);
        Assert.Equal("lyricsplus", rich?.Source.Id);
        Assert.Equal("word", rich?.Format);
        Assert.Equal(2, Assert.Single(rich!.Lines).Words.Count);
        using var jellyfin = JsonDocument.Parse(new JellyfinLyricsProtocolAdapter().Shape(selected!).Body);
        var line = Assert.Single(jellyfin.RootElement.GetProperty("Lyrics").EnumerateArray());
        Assert.Equal("Service words", line.GetProperty("Text").GetString());
        Assert.Equal(10_000_000, line.GetProperty("Start").GetInt64());
        var subsonic = Assert.Single(SubsonicStructuredLyricsMapper.Map(song, selected)!.Lines);
        Assert.Equal((1000L, "Service words"), (subsonic.StartMilliseconds, subsonic.Text));
    }

    private static Mock<IProtocolProviderGateway> Providers(ProtocolExecutionContext context, string[] order, string provider, string format)
    {
        var providers = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        providers.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics)).Returns(order);
        var lyrics = Lyrics(format);
        providers.Setup(item => item.GetLyricsAsync(context, provider, It.IsAny<string>(), ProviderLyricsFormat.LineTimed,
            "Fixture", It.IsAny<IReadOnlyList<string>>(), "Album", 120, null)).ReturnsAsync(new ProviderLyricsResult(
                ProviderLyricsAvailabilityState.Available, provider,
                format == "plain" ? ProviderLyricsFormat.PlainText : ProviderLyricsFormat.LineTimed,
                format == "plain" ? lyrics.PlainLyrics : lyrics.SyncedLyrics, timedLines: lyrics.TimedLines));
        return providers;
    }

    private static LyricsInfo Lyrics(string format) => new()
    {
        PlainLyrics = format == "plain" ? "Service words" : null,
        SyncedLyrics = format == "plain" ? null : "[00:01.000]Service words",
        TimedLines = format == "plain" ? null : [new("Service words", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(119),
            format == "word" ? [new("Service ", TimeSpan.FromSeconds(1)), new("words", TimeSpan.FromSeconds(1.5))] : null)]
    };

    private static Song Song() => new()
    { CanonicalRecordingId = Guid.CreateVersion7(), Title = "Fixture", Artist = "Artist", Album = "Album", Duration = 120 };

    private static ProtocolExecutionContext Context(ProtocolKind kind = ProtocolKind.Jellyfin) => new(kind, "fixture", "listener",
        new AllstarrPrincipal(Guid.CreateVersion7(), kind.ToString().ToLowerInvariant(), "fixture", "listener", "Listener", false),
        "lyrics-quality", DateTimeOffset.UtcNow.AddMinutes(1), default);
}
