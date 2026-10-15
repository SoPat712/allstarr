using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Lyrics;
using Moq;

namespace allstarr.Tests;

public sealed class RecordingLyricsReviewTests
{
    private static readonly Guid RecordingId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid UserId = Guid.Parse("22222222-2222-4222-8222-222222222222");

    [Fact]
    public async Task GetProjectsEverySourceAndUsesProviderMetadata()
    {
        var fixture = new Fixture();
        fixture.SetupIdentity();
        fixture.SetupChoices(new RecordingLyricsChoiceLayers(null, null));
        fixture.Providers.Setup(item => item.GetSongAsync(
                fixture.Context, "allstarr", RecordingId.ToString("D")))
            .ReturnsAsync(fixture.ProviderSong());
        fixture.Library.Setup(item => item.ResolveKnownTrackAsync(
                fixture.Context, fixture.Recording.PublicId, fixture.Context.CancellationToken))
            .ReturnsAsync((string?)null);
        fixture.Lyrics.Setup(item => item.FindAllAsync(
                fixture.Context,
                It.Is<Song>(song => song.CanonicalRecordingId == RecordingId && song.Title == "Provider title"),
                RecordingId.ToString("N"),
                It.IsAny<ProtocolLyricsSources?>()))
            .ReturnsAsync(new List<ProtocolLyricsCandidate>
            {
                new("spotify", new LyricsInfo { SyncedLyrics = "[00:01.234]Timed", Revision = "ignored" }),
                new("backend", new LyricsInfo { PlainLyrics = "Plain words" }),
                new("missing", null)
            });

        var result = await fixture.Service.GetAsync(fixture.Context, RecordingId, "personal");

        Assert.Equal(RecordingId, result.RecordingId);
        Assert.Equal("personal", result.Scope);
        Assert.Equal("Provider title", result.Song.Title);
        Assert.Equal(123, result.Song.DurationSeconds);
        Assert.Equal(
            ["spotify", "backend", "missing"],
            result.Sources.Select(item => item.SourceId));
        Assert.Equal("synced", result.Sources[0].Format);
        Assert.Equal("[00:01.234]Timed", result.Sources[0].Preview);
        Assert.True(result.Sources[0].Available);
        Assert.Equal("plain", result.Sources[1].Format);
        Assert.Equal("Plain words", result.Sources[1].Preview);
        Assert.False(result.Sources[2].Available);
        Assert.Null(result.Sources[2].Format);
        Assert.Null(result.Sources[2].Preview);
    }

    [Fact]
    public async Task PreviewHashUsesFullContentAndTruncatesAtUnicodeBoundary()
    {
        var fixture = new Fixture();
        fixture.SetupIdentity();
        fixture.SetupChoices(new RecordingLyricsChoiceLayers(null, null));
        fixture.Providers.Setup(item => item.GetSongAsync(
                fixture.Context, "allstarr", RecordingId.ToString("D")))
            .ReturnsAsync(fixture.ProviderSong());
        fixture.Library.Setup(item => item.ResolveKnownTrackAsync(
                fixture.Context, fixture.Recording.PublicId, fixture.Context.CancellationToken))
            .ReturnsAsync((string?)null);
        var full = new string('x', 32767) + "😀" + "tail";
        fixture.Lyrics.Setup(item => item.FindAllAsync(
                fixture.Context, It.IsAny<Song>(), RecordingId.ToString("N"), It.IsAny<ProtocolLyricsSources?>()))
            .ReturnsAsync(new List<ProtocolLyricsCandidate>
            {
                new("lrclib", new LyricsInfo { PlainLyrics = full })
            });

        var result = await fixture.Service.GetAsync(fixture.Context, RecordingId, "personal");
        var source = Assert.Single(result.Sources);
        var expectedHash = "sha256:" + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(RecordingId.ToString("D") + "\nlrclib\nplain\n" + full))).ToLowerInvariant();

        Assert.Equal(new string('x', 32767), source.Preview);
        Assert.True(source.Truncated);
        Assert.Equal(expectedHash, source.PreviewRevision);
        Assert.DoesNotContain("tail", source.Preview ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackendCallbackReceivesOnlyVerifiedNativeItemId()
    {
        var fixture = new Fixture();
        fixture.SetupIdentity();
        fixture.SetupChoices(new RecordingLyricsChoiceLayers(null, null));
        fixture.Providers.Setup(item => item.GetSongAsync(
                fixture.Context, "allstarr", RecordingId.ToString("D")))
            .ReturnsAsync(fixture.ProviderSong());
        fixture.Library.Setup(item => item.ResolveKnownTrackAsync(
                fixture.Context, fixture.Recording.PublicId, fixture.Context.CancellationToken))
            .ReturnsAsync("native-42");
        fixture.Library.Setup(item => item.FindByBackendItemAsync(
                fixture.Context, "native-42", fixture.Context.CancellationToken))
            .ReturnsAsync(fixture.NativeTrack(RecordingId, "native-42"));
        string? callbackId = null;
        fixture.Lyrics.Setup(item => item.FindAllAsync(
                fixture.Context, It.IsAny<Song>(), RecordingId.ToString("N"), It.IsAny<ProtocolLyricsSources?>()))
            .Returns((ProtocolExecutionContext _, Song _, string _, ProtocolLyricsSources? sources) =>
                InvokeBackendAsync(sources, id => callbackId = id));

        var result = await fixture.Service.GetAsync(
            fixture.Context,
            RecordingId,
            "personal",
            (id, _) =>
            {
                Assert.Equal("native-42", id);
                return Task.FromResult<LyricsInfo?>(new() { SyncedLyrics = "[00:01.000]Native" });
            });

        Assert.Equal("native-42", callbackId);
        Assert.Equal("backend", Assert.Single(result.Sources).SourceId);
        Assert.Equal("[00:01.000]Native", result.Sources[0].Preview);
    }

    [Fact]
    public async Task WrongNativeAssociationDoesNotSupplyBackendCallback()
    {
        var fixture = new Fixture();
        fixture.SetupIdentity();
        fixture.SetupChoices(new RecordingLyricsChoiceLayers(null, null));
        fixture.Providers.Setup(item => item.GetSongAsync(
                fixture.Context, "allstarr", RecordingId.ToString("D")))
            .ReturnsAsync(fixture.ProviderSong());
        fixture.Library.Setup(item => item.ResolveKnownTrackAsync(
                fixture.Context, fixture.Recording.PublicId, fixture.Context.CancellationToken))
            .ReturnsAsync("wrong-native");
        fixture.Library.Setup(item => item.FindByBackendItemAsync(
                fixture.Context, "wrong-native", fixture.Context.CancellationToken))
            .ReturnsAsync(fixture.NativeTrack(Guid.Parse("33333333-3333-4333-8333-333333333333"), "wrong-native"));
        var backendWasAvailable = true;
        fixture.Lyrics.Setup(item => item.FindAllAsync(
                fixture.Context, It.IsAny<Song>(), RecordingId.ToString("N"), It.IsAny<ProtocolLyricsSources?>()))
            .Returns((ProtocolExecutionContext _, Song _, string _, ProtocolLyricsSources? sources) =>
            {
                backendWasAvailable = sources?.Backend != null;
                return Task.FromResult<IReadOnlyList<ProtocolLyricsCandidate>>(
                    [new("spotify", new LyricsInfo { PlainLyrics = "Provider words" })]);
            });

        var result = await fixture.Service.GetAsync(
            fixture.Context,
            RecordingId,
            "personal",
            (_, _) => throw new InvalidOperationException("The unverified native item was read."));

        Assert.False(backendWasAvailable);
        Assert.Equal("Provider words", Assert.Single(result.Sources).Preview);
    }

    [Fact]
    public async Task WrongProviderCanonicalIdentityIsRejected()
    {
        var fixture = new Fixture();
        fixture.SetupIdentity();
        fixture.SetupChoices(new RecordingLyricsChoiceLayers(null, null));
        fixture.Providers.Setup(item => item.GetSongAsync(
                fixture.Context, "allstarr", RecordingId.ToString("D")))
            .ReturnsAsync(fixture.ProviderSong(
                Guid.Parse("33333333-3333-4333-8333-333333333333")));
        fixture.Library.Setup(item => item.ResolveKnownTrackAsync(
                fixture.Context, fixture.Recording.PublicId, fixture.Context.CancellationToken))
            .ReturnsAsync((string?)null);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Service.GetAsync(
            fixture.Context, RecordingId, "personal"));
        fixture.Lyrics.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProviderMetadataFailureFallsBackToAuthorizedNativeSong()
    {
        var fixture = new Fixture();
        fixture.SetupIdentity();
        fixture.SetupChoices(new RecordingLyricsChoiceLayers(null, null));
        fixture.Providers.Setup(item => item.GetSongAsync(
                fixture.Context, "allstarr", RecordingId.ToString("D")))
            .ThrowsAsync(new HttpRequestException("synthetic provider outage"));
        fixture.Library.Setup(item => item.ResolveKnownTrackAsync(
                fixture.Context, fixture.Recording.PublicId, fixture.Context.CancellationToken))
            .ReturnsAsync("native-fallback");
        fixture.Library.Setup(item => item.FindByBackendItemAsync(
                fixture.Context, "native-fallback", fixture.Context.CancellationToken))
            .ReturnsAsync(fixture.NativeTrack(RecordingId, "native-fallback"));
        fixture.Lyrics.Setup(item => item.FindAllAsync(
                fixture.Context, It.Is<Song>(song => song.IsLocal && song.Id == "native-fallback"),
                RecordingId.ToString("N"), It.IsAny<ProtocolLyricsSources?>()))
            .Returns((ProtocolExecutionContext _, Song _, string _, ProtocolLyricsSources? sources) =>
                InvokeBackendAsync(sources, null));

        var result = await fixture.Service.GetAsync(
            fixture.Context,
            RecordingId,
            "personal",
            (_, _) => Task.FromResult<LyricsInfo?>(new() { PlainLyrics = "Native fallback" }));

        Assert.Equal("Native title", result.Song.Title);
        Assert.Equal("Native artist", result.Song.Artist);
        Assert.Equal("Native fallback", result.Sources[0].Preview);
    }

    [Fact]
    public async Task MissingProviderAndNativeMetadataReturnsSafeNotFound()
    {
        var fixture = new Fixture();
        fixture.SetupIdentity();
        fixture.SetupChoices(new RecordingLyricsChoiceLayers(null, null));
        fixture.Providers.Setup(item => item.GetSongAsync(
                fixture.Context, "allstarr", RecordingId.ToString("D")))
            .ReturnsAsync((Song?)null);
        fixture.Library.Setup(item => item.ResolveKnownTrackAsync(
                fixture.Context, fixture.Recording.PublicId, fixture.Context.CancellationToken))
            .ReturnsAsync((string?)null);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Service.GetAsync(
            fixture.Context, RecordingId, "personal"));
        fixture.Lyrics.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ChooseRefreshesPreviewAndWritesOnlyMatchingRevision()
    {
        var fixture = new Fixture();
        fixture.SetupIdentity();
        var layers = new RecordingLyricsChoiceLayers(null, null);
        fixture.SetupChoices(layers);
        fixture.Providers.Setup(item => item.GetSongAsync(
                fixture.Context, "allstarr", RecordingId.ToString("D")))
            .ReturnsAsync(fixture.ProviderSong());
        fixture.Library.Setup(item => item.ResolveKnownTrackAsync(
                fixture.Context, fixture.Recording.PublicId, fixture.Context.CancellationToken))
            .ReturnsAsync((string?)null);
        fixture.Lyrics.Setup(item => item.FindAllAsync(
                fixture.Context, It.IsAny<Song>(), RecordingId.ToString("N"), It.IsAny<ProtocolLyricsSources?>()))
            .ReturnsAsync(new List<ProtocolLyricsCandidate>
            {
                new("spotify", new LyricsInfo { PlainLyrics = "Fresh words" })
            });
        var expected = new LyricsChoiceRevision();
        var saved = new RecordingLyricsChoiceLayers(
            new RecordingLyricsChoice(Guid.CreateVersion7(), RecordingId, UserId, "spotify", 1), null);
        fixture.Choices.Setup(item => item.SetAsync(
                fixture.Context, RecordingId, "spotify", "personal", expected))
            .ReturnsAsync(saved);
        var previewRevision = Hash(RecordingId, "spotify", "plain", "Fresh words");

        var result = await fixture.Service.ChooseAsync(
            fixture.Context, RecordingId, "spotify", "personal", expected, previewRevision);

        Assert.Equal(saved, result);
        fixture.Choices.Verify(item => item.SetAsync(
            fixture.Context, RecordingId, "spotify", "personal", expected), Times.Once);
    }

    [Fact]
    public async Task ChooseRejectsStalePreviewWithoutWriting()
    {
        var fixture = new Fixture();
        fixture.SetupIdentity();
        fixture.SetupChoices(new RecordingLyricsChoiceLayers(null, null));
        fixture.Providers.Setup(item => item.GetSongAsync(
                fixture.Context, "allstarr", RecordingId.ToString("D")))
            .ReturnsAsync(fixture.ProviderSong());
        fixture.Library.Setup(item => item.ResolveKnownTrackAsync(
                fixture.Context, fixture.Recording.PublicId, fixture.Context.CancellationToken))
            .ReturnsAsync((string?)null);
        fixture.Lyrics.Setup(item => item.FindAllAsync(
                fixture.Context, It.IsAny<Song>(), RecordingId.ToString("N"), It.IsAny<ProtocolLyricsSources?>()))
            .ReturnsAsync(new List<ProtocolLyricsCandidate>
            {
                new("spotify", new LyricsInfo { PlainLyrics = "Current words" })
            });

        await Assert.ThrowsAsync<LyricsChoiceConflictException>(() => fixture.Service.ChooseAsync(
            fixture.Context,
            RecordingId,
            "spotify",
            "personal",
            new LyricsChoiceRevision(),
            "sha256:stale"));
        fixture.Choices.Verify(item => item.SetAsync(
            It.IsAny<ProtocolExecutionContext>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<LyricsChoiceRevision>()), Times.Never);
    }

    [Fact]
    public async Task ClearUsesPersistenceOnlyAndDoesNotReadNetwork()
    {
        var fixture = new Fixture();
        var expected = new LyricsChoiceRevision();
        var cleared = new RecordingLyricsChoiceLayers(null, null);
        fixture.Choices.Setup(item => item.ClearAsync(
                fixture.Context, RecordingId, "household", expected))
            .ReturnsAsync(cleared);

        var result = await fixture.Service.ClearAsync(
            fixture.Context, RecordingId, "household", expected);

        Assert.Equal(cleared, result);
        fixture.Providers.VerifyNoOtherCalls();
        fixture.Library.VerifyNoOtherCalls();
        fixture.Lyrics.VerifyNoOtherCalls();
        fixture.Choices.Verify(item => item.ClearAsync(
            fixture.Context, RecordingId, "household", expected), Times.Once);
    }

    [Fact]
    public async Task RejectsUnlinkedActorAndMalformedScope()
    {
        var fixture = new Fixture();
        var unlinked = new ProtocolExecutionContext(
            ProtocolKind.Jellyfin,
            "backend",
            "anonymous",
            null,
            "review",
            fixture.Context.Deadline,
            CancellationToken.None);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.GetAsync(
            unlinked, RecordingId, "personal"));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.GetAsync(
            fixture.Context, RecordingId, "global"));
    }

    [Fact]
    public async Task CancellationPropagatesBeforeProviderOrLibraryAccess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var fixture = new Fixture(cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.GetAsync(
            fixture.Context, RecordingId, "personal"));
        fixture.Providers.VerifyNoOtherCalls();
        fixture.Library.VerifyNoOtherCalls();
        fixture.Lyrics.VerifyNoOtherCalls();
    }

    private static async Task<IReadOnlyList<ProtocolLyricsCandidate>> InvokeBackendAsync(
        ProtocolLyricsSources? sources,
        Action<string>? capture)
    {
        Assert.NotNull(sources?.Backend);
        var lyrics = await sources!.Backend!(CancellationToken.None);
        capture?.Invoke("native-42");
        return [new ProtocolLyricsCandidate("backend", lyrics)];
    }

    private static string Hash(Guid recordingId, string sourceId, string format, string content) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            recordingId.ToString("D") + "\n" + sourceId + "\n" + format + "\n" + content))).ToLowerInvariant();

    private sealed class Fixture
    {
        public Fixture(CancellationToken cancellationToken = default)
        {
            Context = new ProtocolExecutionContext(
                ProtocolKind.Jellyfin,
                "backend",
                "listener",
                new AllstarrPrincipal(UserId, "jellyfin", "backend", "listener", "Listener", false),
                "review",
                new DateTimeOffset(2026, 10, 15, 16, 0, 0, TimeSpan.Zero).AddMinutes(1),
                cancellationToken);
            Recording = new CanonicalRecordingIdentity(
                RecordingId, UserId, null, null, 1)
            { PublicId = "ext-allstarr-song-11111111-1111-4111-8111-111111111111" };
            Identities = new Mock<ITrackIdentityService>(MockBehavior.Strict);
            Providers = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
            Library = new Mock<ILibraryIndexService>(MockBehavior.Strict);
            Lyrics = new Mock<IProtocolLyricsResolver>(MockBehavior.Strict);
            Choices = new Mock<IRecordingLyricsChoices>(MockBehavior.Strict);
            Service = new RecordingLyricsReview(
                Identities.Object,
                Providers.Object,
                Library.Object,
                Lyrics.Object,
                Choices.Object);
        }

        public ProtocolExecutionContext Context { get; }
        public CanonicalRecordingIdentity Recording { get; }
        public Mock<ITrackIdentityService> Identities { get; }
        public Mock<IProtocolProviderGateway> Providers { get; }
        public Mock<ILibraryIndexService> Library { get; }
        public Mock<IProtocolLyricsResolver> Lyrics { get; }
        public Mock<IRecordingLyricsChoices> Choices { get; }
        public RecordingLyricsReview Service { get; }

        public void SetupIdentity() => Identities.Setup(item => item.ResolveRecordingAsync(
                It.Is<ProviderActorContext>(actor => actor.EffectiveUserId == UserId),
                RecordingId.ToString("D"),
                Context.CancellationToken))
            .ReturnsAsync(Recording);

        public void SetupChoices(RecordingLyricsChoiceLayers layers) => Choices.Setup(item => item.GetAsync(
                Context, RecordingId)).ReturnsAsync(layers);

        public Song ProviderSong(Guid? canonicalRecordingId = null) => new()
        {
            Id = "provider-song",
            CanonicalRecordingId = canonicalRecordingId ?? RecordingId,
            Title = "Provider title",
            Artist = "Provider artist",
            Album = "Provider album",
            Duration = 123,
            PreciseDuration = TimeSpan.FromMilliseconds(123_450),
            Artists = ["Provider artist"],
            IsLocal = false
        };

        public IndexedLibraryTrack NativeTrack(Guid canonicalRecordingId, string backendItemId) =>
            new(
                Guid.Parse("44444444-4444-4444-8444-444444444444"),
                backendItemId,
                "/never-read/native.flac",
                "Native title",
                "Native artist",
                "Native album",
                null,
                123_000,
                "fixture",
                null,
                null,
                null,
                canonicalRecordingId,
                new Dictionary<string, string> { ["spotify"] = "native-provider-id" },
                Context.Deadline.AddMinutes(-1),
                Context.Deadline.AddMinutes(-1),
                1);
    }
}
