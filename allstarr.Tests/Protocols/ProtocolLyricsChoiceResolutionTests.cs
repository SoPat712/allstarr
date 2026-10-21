using System.Collections.Immutable;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Models.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed class ProtocolLyricsChoiceResolutionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SavedManualOnlyChoiceIsUsedDuringPlayback(bool personal)
    {
        var context = Context();
        var recording = Guid.CreateVersion7();
        var provider = Providers(context, true);
        var preference = new RecordingLyricsChoice(Guid.CreateVersion7(), recording,
            personal ? context.Principal!.UserId : null, "lrclib", 2);
        var choices = Choices(context, recording, new(personal ? preference : null, personal ? null : preference));
        var policies = new Mock<IEffectiveProviderPolicyResolver>(MockBehavior.Strict);
        policies.Setup(item => item.ResolveForUserAsync(context.Principal!.UserId, context.CancellationToken))
            .ReturnsAsync(new EffectiveProviderPolicySnapshot(
                ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty.Add(ProviderCapabilityKind.Lyrics, ["spotify", "lrclib"]),
                ImmutableHashSet<string>.Empty, "lossless", .07)
            { ProviderModes = ImmutableDictionary<string, ProviderUseMode>.Empty.Add("lrclib", ProviderUseMode.ManualOnly) });
        var result = await new ProtocolLyricsResolver(provider.Object, NullLogger<ProtocolLyricsResolver>.Instance,
            effectivePolicies: policies.Object, choices: choices.Object).FindAsync(context, Song(recording), "source", "spotify", "exact");
        Assert.Equal("lrclib", result?.Source);
        Assert.Equal("Chosen plain words", result?.PlainLyrics);
    }

    [Fact]
    public async Task HouseholdPublicationUsesHouseholdPreferenceAndPolicyWithoutPersonalNativeContent()
    {
        var context = Context();
        var recording = Guid.CreateVersion7();
        var choices = Choices(context, recording, new(new(Guid.CreateVersion7(), recording, context.Principal!.UserId, "lrclib", 1),
            new(Guid.CreateVersion7(), recording, null, "spotify", 1)));
        var policies = new Mock<IEffectiveProviderPolicyResolver>(MockBehavior.Strict);
        policies.Setup(item => item.ResolveAsync(context.CancellationToken)).ReturnsAsync(new EffectiveProviderPolicySnapshot(
            new Dictionary<ProviderCapabilityKind, ImmutableArray<string>>
            { [ProviderCapabilityKind.Lyrics] = ["backend", "spotify", "lrclib"] }.ToImmutableDictionary(),
            ImmutableHashSet<string>.Empty, "lossless", .07));
        var providers = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        providers.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics)).Returns(["backend", "spotify", "lrclib"]);
        providers.Setup(item => item.GetHouseholdLyricsAsync(context, "spotify", "exact", ProviderLyricsFormat.LineTimed,
            "Fixture", It.IsAny<IReadOnlyList<string>>(), "Album", 120, null)).ReturnsAsync(new ProviderLyricsResult(
                ProviderLyricsAvailabilityState.Available, "spotify", ProviderLyricsFormat.PlainText, "Shared chosen words"));
        var nativeCalls = 0;
        var result = await new ProtocolLyricsResolver(providers.Object, NullLogger<ProtocolLyricsResolver>.Instance,
            effectivePolicies: policies.Object, choices: choices.Object).FindAsync(context, Song(recording), "recording",
            new ProtocolLyricsSources(Backend: _ =>
            {
                nativeCalls++;
                return Task.FromResult<allstarr.Models.Lyrics.LyricsInfo?>(new() { SyncedLyrics = "[00:01.234]Personal native content" });
            }, HouseholdOnly: true));
        Assert.Equal("spotify", result?.Source);
        Assert.Equal("Shared chosen words", result?.PlainLyrics);
        Assert.Equal(0, nativeCalls);
        policies.Verify(item => item.ResolveForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        providers.Verify(item => item.GetLyricsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<ProviderLyricsFormat?>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<int?>(), null), Times.Never);
    }

    [Fact]
    public async Task HouseholdPreviewRetainsAnUnavailableBackendRowWithoutReadingPersonalContent()
    {
        var context = Context();
        var providers = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        providers.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics)).Returns(["backend"]);
        var calls = 0;
        var results = await new ProtocolLyricsResolver(providers.Object, NullLogger<ProtocolLyricsResolver>.Instance)
            .FindAllAsync(context, Song(null), "recording", new ProtocolLyricsSources(Backend: _ =>
            {
                calls++;
                return Task.FromResult<allstarr.Models.Lyrics.LyricsInfo?>(new() { PlainLyrics = "Personal words" });
            }, HouseholdOnly: true));
        Assert.Null(Assert.Single(results).Lyrics);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExplicitRecordingChoiceWinsEvenWhenItIsPlain(bool personal)
    {
        var context = Context();
        var recording = Guid.CreateVersion7();
        var provider = Providers(context, true);
        var preference = new RecordingLyricsChoice(Guid.CreateVersion7(), recording,
            personal ? context.Principal!.UserId : null, "lrclib", 2);
        var choices = Choices(context, recording, new(personal ? preference : null, personal ? null : preference));
        var result = await Resolver(provider, choices).FindAsync(context, Song(recording), "source", "spotify", "exact");
        Assert.Equal("lrclib", result?.Source);
        Assert.Equal("Chosen plain words", result?.PlainLyrics);
        provider.Verify(item => item.GetLyricsAsync(context, "spotify", It.IsAny<string>(), It.IsAny<ProviderLyricsFormat?>(),
            It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<int?>(), null), Times.Never);
        choices.VerifyAll();
    }

    [Fact]
    public async Task UnavailableChoiceFallsBackWithoutDeletingThePreference()
    {
        var context = Context();
        var recording = Guid.CreateVersion7();
        var choices = Choices(context, recording, new(new(Guid.CreateVersion7(), recording, context.Principal!.UserId, "lrclib", 2), null));
        var provider = Providers(context, false);
        var result = await Resolver(provider, choices).FindAsync(context, Song(recording), "source", "spotify", "exact");
        Assert.Equal("spotify", result?.Source);
        choices.Verify(item => item.GetAsync(context, recording), Times.Once);
        choices.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DisabledChoiceCannotBypassPolicy()
    {
        var context = Context();
        var recording = Guid.CreateVersion7();
        var choices = Choices(context, recording, new(new(Guid.CreateVersion7(), recording, context.Principal!.UserId, "lrclib", 2), null));
        var provider = Providers(context, true);
        var policies = new Mock<IEffectiveProviderPolicyResolver>(MockBehavior.Strict);
        policies.Setup(item => item.ResolveForUserAsync(context.Principal.UserId, context.CancellationToken))
            .ReturnsAsync(new EffectiveProviderPolicySnapshot(
                new Dictionary<ProviderCapabilityKind, ImmutableArray<string>>
                { [ProviderCapabilityKind.Lyrics] = ["spotify", "lrclib"] }.ToImmutableDictionary(),
                ImmutableHashSet.Create("lrclib"), "lossless", .07));
        var result = await new ProtocolLyricsResolver(provider.Object, NullLogger<ProtocolLyricsResolver>.Instance,
            effectivePolicies: policies.Object, choices: choices.Object).FindAsync(context, Song(recording), "source", "spotify", "exact");
        Assert.Equal("spotify", result?.Source);
        provider.Verify(item => item.GetLyricsAsync(context, "lrclib", It.IsAny<string>(), It.IsAny<ProviderLyricsFormat?>(),
            It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<int?>(), null), Times.Never);
    }

    [Fact]
    public async Task NativeChoiceIdentityComesFromTheAuthorizedIndexInsteadOfCallerMetadata()
    {
        var context = Context();
        var recording = Guid.CreateVersion7();
        var choices = Choices(context, recording, new(new(Guid.CreateVersion7(), recording, context.Principal!.UserId, "lrclib", 2), null));
        var index = new Mock<ILibraryIndexService>(MockBehavior.Strict);
        index.Setup(item => item.FindByBackendItemAsync(context, "native", context.CancellationToken))
            .ReturnsAsync(new IndexedLibraryTrack(Guid.CreateVersion7(), "native", "/never-read.flac", "Fixture", "Artist",
                "Album", null, null, null, null, null, null, recording, new Dictionary<string, string>(),
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0));
        var song = Song(Guid.CreateVersion7());
        song.Id = "native";
        song.IsLocal = true;
        var provider = Providers(context, true);
        var result = await new ProtocolLyricsResolver(provider.Object, NullLogger<ProtocolLyricsResolver>.Instance,
            libraryIndex: index.Object, choices: choices.Object).FindAsync(context, song, "native");
        Assert.Equal("lrclib", result?.Source);
        choices.VerifyAll();
        index.VerifyAll();
    }

    [Fact]
    public async Task MetadataRequestHashNeverBecomesRecordingAuthority()
    {
        var context = Context();
        var choices = new Mock<IRecordingLyricsChoices>(MockBehavior.Strict);
        var provider = Providers(context, true);
        var result = await Resolver(provider, choices).FindAsync(context, Song(null), Guid.CreateVersion7().ToString("D"), "spotify", "exact");
        Assert.Equal("spotify", result?.Source);
        choices.VerifyNoOtherCalls();
    }

    private static ProtocolLyricsResolver Resolver(Mock<IProtocolProviderGateway> providers, Mock<IRecordingLyricsChoices> choices) =>
        new(providers.Object, NullLogger<ProtocolLyricsResolver>.Instance, choices: choices.Object);
    private static Mock<IRecordingLyricsChoices> Choices(ProtocolExecutionContext context, Guid recording, RecordingLyricsChoiceLayers result)
    {
        var choices = new Mock<IRecordingLyricsChoices>(MockBehavior.Strict);
        choices.Setup(item => item.GetAsync(context, recording)).ReturnsAsync(result);
        return choices;
    }
    private static Mock<IProtocolProviderGateway> Providers(ProtocolExecutionContext context, bool chosenAvailable)
    {
        var providers = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        providers.Setup(item => item.GetProviderOrder(ProviderCapabilityKind.Lyrics)).Returns(["spotify", "lrclib"]);
        providers.Setup(item => item.GetLyricsAsync(context, "spotify", It.IsAny<string>(), ProviderLyricsFormat.LineTimed,
            "Fixture", It.IsAny<IReadOnlyList<string>>(), "Album", 120, null)).ReturnsAsync(new ProviderLyricsResult(
                ProviderLyricsAvailabilityState.Available, "spotify", ProviderLyricsFormat.LineTimed, "[00:01.234]Automatic words"));
        providers.Setup(item => item.GetLyricsAsync(context, "lrclib", It.IsAny<string>(), ProviderLyricsFormat.LineTimed,
            "Fixture", It.IsAny<IReadOnlyList<string>>(), "Album", 120, null)).ReturnsAsync(chosenAvailable
                ? new ProviderLyricsResult(ProviderLyricsAvailabilityState.Available, "lrclib", ProviderLyricsFormat.PlainText, "Chosen plain words") : null);
        return providers;
    }
    private static Song Song(Guid? recording) => new()
    {
        Id = "ext-spotify-song-exact",
        CanonicalRecordingId = recording,
        Title = "Fixture",
        Artist = "Artist",
        Album = "Album",
        Duration = 120,
        ExternalProvider = "spotify",
        ExternalId = "exact"
    };
    private static ProtocolExecutionContext Context() => new(ProtocolKind.Jellyfin, "backend", "listener",
        new AllstarrPrincipal(Guid.CreateVersion7(), "jellyfin", "backend", "listener", "Listener", false),
        "lyrics-choice", DateTimeOffset.UtcNow.AddMinutes(1), default);
}
