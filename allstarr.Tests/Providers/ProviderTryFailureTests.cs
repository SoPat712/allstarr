using allstarr.Core.Capabilities;
using allstarr.Services.Common;
using Moq;

namespace allstarr.Tests;

public sealed class ProviderTryFailureTests
{
    [Fact]
    public async Task LyricsChallengeKeepsSafeGuidanceInTestResults()
    {
        var lyrics = new Mock<IProviderLyricsCapability>();
        lyrics.Setup(item => item.FetchLyricsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderLyricsRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderLyricsResult>.Failure(ProviderError.BlockedByChallenge()));
        var registry = new Mock<IProviderRegistry>();
        registry.Setup(item => item.GetRequiredCapability<IProviderLyricsCapability>("fixture", ProviderCapabilityKind.Lyrics)).Returns(lyrics.Object);
        var result = await new ProviderTryOperations(registry.Object).ExecuteAsync(ProviderTryOperationsTests.Context(),
            new() { Capability = "lyrics", Operation = "lyrics", Title = "Fixture", Artist = "Artist" });
        Assert.Equal("blocked-by-challenge", result.ReasonCode);
        Assert.Equal(ProviderError.BlockedByChallenge().SafeMessage, result.Message);
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (var operation in new[] { "tracks", "albums", "artists", "track", "isrc", "lyrics", "playlist", "mine" })
            foreach (var failure in new[] { "not_found", "provider_error", "timeout" })
                yield return [operation, failure];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Operations_ConvertFailuresWithoutProviderText(string operation, string failure)
    {
        Task<ProviderOutcome<T>> Outcome<T>()
        {
            if (failure == "timeout") throw new OperationCanceledException();
            return Task.FromResult(ProviderOutcome<T>.Failure(new(failure == "not_found" ? ProviderErrorKind.NotFound : ProviderErrorKind.TransientFailure)));
        }
        var metadata = new Mock<IProviderMetadataCapability>();
        metadata.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .Returns(() => Outcome<ProviderPage<ProviderTrackMetadata>>());
        metadata.Setup(item => item.SearchAlbumsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .Returns(() => Outcome<ProviderPage<ProviderAlbumMetadata>>());
        metadata.Setup(item => item.SearchArtistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .Returns(() => Outcome<ProviderPage<ProviderArtistMetadata>>());
        metadata.Setup(item => item.GetTrackAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderTrackLookupRequest>()))
            .Returns(() => Outcome<ProviderTrackMetadata>());
        metadata.Setup(item => item.LookupByIsrcAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderIsrcLookupRequest>()))
            .Returns(() => Outcome<ProviderTrackMetadata>());
        var lyrics = new Mock<IProviderLyricsCapability>();
        lyrics.Setup(item => item.FetchLyricsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderLyricsRequest>()))
            .Returns(() => Outcome<ProviderLyricsResult>());
        var playlist = new Mock<IProviderPlaylistCapability>();
        playlist.Setup(item => item.GetPlaylistTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderPlaylistTracksRequest>()))
            .Returns(() => Outcome<ProviderPlaylistTrackPage>());
        playlist.Setup(item => item.GetUserPlaylistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderUserPlaylistsRequest>()))
            .Returns(() => Outcome<ProviderPage<ProviderPlaylistSummary>>());
        var registry = new Mock<IProviderRegistry>();
        registry.Setup(item => item.GetRequiredCapability<IProviderMetadataCapability>("fixture", ProviderCapabilityKind.Metadata)).Returns(metadata.Object);
        registry.Setup(item => item.GetRequiredCapability<IProviderLyricsCapability>("fixture", ProviderCapabilityKind.Lyrics)).Returns(lyrics.Object);
        registry.Setup(item => item.GetRequiredCapability<IProviderPlaylistCapability>("fixture", ProviderCapabilityKind.Playlist)).Returns(playlist.Object);
        var search = operation is "tracks" or "albums" or "artists";
        var capability = operation == "lyrics" ? "lyrics" : operation is "playlist" or "mine" ? "playlist" : "metadata";
        var result = await new ProviderTryOperations(registry.Object).ExecuteAsync(ProviderTryOperationsTests.Context(), new()
        {
            Capability = capability,
            Operation = search ? "search" : operation,
            Kind = search ? operation : null,
            Query = "sample",
            Id = "track",
            Isrc = "USQX91300108",
            Title = "Song",
            Artist = "Artist",
            Url = "playlist"
        });
        Assert.False(result.Succeeded);
        Assert.Equal(failure == "provider_error" ? "transient-failure" : failure, result.ReasonCode);
        Assert.NotNull(result.Message);
    }
}
