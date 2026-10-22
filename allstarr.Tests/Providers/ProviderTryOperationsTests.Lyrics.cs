using allstarr.Core.Capabilities;
using allstarr.Services.Common;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProviderTryOperationsTests
{
    [Fact]
    public void LyricsCatalog_AdvertisesLookupMetadata()
    {
        var catalog = ProviderTryOperations.Describe(Descriptor(new ProviderCapabilityDescriptor(
            ProviderCapabilityKind.Lyrics, ProviderCapabilitySupportState.Supported,
            ProviderAccountRequirement.None, "1", ["fetchLyrics"])), null);
        var operation = Assert.Single(Assert.Single(catalog.Capabilities).Operations);
        Assert.Equal("lyrics", operation.Id);
        Assert.Equal(["title", "artist", "album", "durationSeconds", "isrc"], operation.Inputs);
    }

    [Theory]
    [InlineData("Fixture", null)]
    [InlineData(null, "USQX91300108")]
    public void LyricsValidation_AcceptsTitleOrIsrcAlone(string? title, string? isrc)
    {
        Assert.Null(new ProviderTryRequest { Capability = "lyrics", Operation = "lyrics", Title = title, Isrc = isrc }.Validate());
    }

    [Theory]
    [InlineData("Fixture", null, null)]
    [InlineData(null, "USQX91300108", null)]
    [InlineData("Fixture", "USQX91300108", "Artist, Guest")]
    public async Task LyricsLookup_AcceptsOptionalMetadataAndPassesEverySuppliedValue(string? title, string? isrc, string? artist)
    {
        var request = new ProviderTryRequest
        {
            Capability = "lyrics",
            Operation = "lyrics",
            Title = title,
            Artist = artist,
            Isrc = isrc,
            Album = "Album",
            DurationSeconds = 180,
            TrackId = "native-track"
        };
        Assert.Null(request.Validate());
        var lyrics = new Mock<IProviderLyricsCapability>(MockBehavior.Strict);
        lyrics.Setup(item => item.FetchLyricsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderLyricsRequest>()))
            .Returns((ProviderExecutionContext _, ProviderLyricsRequest lookup) =>
            {
                Assert.Equal(title, lookup.TrackTitle);
                Assert.Equal(isrc, lookup.Isrc);
                Assert.Equal(artist == null ? [] : new[] { "Artist", "Guest" }, lookup.ArtistNames);
                Assert.Equal("Album", lookup.AlbumTitle);
                Assert.Equal(180, lookup.DurationSeconds);
                Assert.Equal("native-track", lookup.ProviderTrackId.Value);
                return Task.FromResult(ProviderOutcome<ProviderLyricsResult>.Success(new(
                    ProviderLyricsAvailabilityState.Available, "fixture", ProviderLyricsFormat.PlainText, "Words")));
            });
        var registry = new Mock<IProviderRegistry>();
        registry.Setup(item => item.GetRequiredCapability<IProviderLyricsCapability>("fixture", ProviderCapabilityKind.Lyrics)).Returns(lyrics.Object);
        var result = await new ProviderTryOperations(registry.Object).ExecuteAsync(Context(), request);
        Assert.True(result.Succeeded);
        lyrics.VerifyAll();
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(" ", " ", null)]
    [InlineData(null, null, "native-track")]
    public void LyricsValidation_RequiresTitleOrIsrc(string? title, string? isrc, string? trackId)
    {
        Assert.Equal("A title or ISRC is required.", new ProviderTryRequest
        { Operation = "lyrics", Title = title, Isrc = isrc, TrackId = trackId }.Validate());
    }

    [Theory]
    [InlineData("title")]
    [InlineData("artist")]
    [InlineData("album")]
    public void LyricsValidation_BoundsMetadataText(string field)
    {
        ProviderTryRequest Request(int length) => new()
        {
            Operation = "lyrics",
            Title = field == "title" ? new string('x', length) : "Fixture",
            Artist = field == "artist" ? new string('x', length) : null,
            Album = field == "album" ? new string('x', length) : null
        };
        Assert.Null(Request(500).Validate());
        Assert.Equal("Text inputs must be at most 500 characters.", Request(501).Validate());
    }

    [Fact]
    public void LyricsValidation_BoundsIsrc()
    {
        Assert.Null(new ProviderTryRequest { Operation = "lyrics", Isrc = new string('x', 20) }.Validate());
        Assert.Equal("An ISRC must be at most 20 characters.",
            new ProviderTryRequest { Operation = "lyrics", Isrc = new string('x', 21) }.Validate());
    }

    [Theory]
    [InlineData(-1, "Duration must be between zero and 86400 seconds.")]
    [InlineData(0, null)]
    [InlineData(86400, null)]
    [InlineData(86401, "Duration must be between zero and 86400 seconds.")]
    public void LyricsValidation_BoundsDuration(int duration, string? expected)
    {
        Assert.Equal(expected, new ProviderTryRequest { Operation = "lyrics", Title = "Fixture", DurationSeconds = duration }.Validate());
    }

    [Theory]
    [InlineData(ProviderErrorKind.NotFound)]
    [InlineData(ProviderErrorKind.PermanentFailure)]
    [InlineData(ProviderErrorKind.TransientFailure)]
    public async Task LyricsLookup_WithoutArtistKeepsUnrelatedFailureReasons(ProviderErrorKind kind)
    {
        var error = new ProviderError(kind);
        var lyrics = new Mock<IProviderLyricsCapability>();
        lyrics.Setup(item => item.FetchLyricsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderLyricsRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderLyricsResult>.Failure(error));
        var registry = new Mock<IProviderRegistry>();
        registry.Setup(item => item.GetRequiredCapability<IProviderLyricsCapability>("fixture", ProviderCapabilityKind.Lyrics)).Returns(lyrics.Object);
        var result = await new ProviderTryOperations(registry.Object).ExecuteAsync(Context(),
            new() { Capability = "lyrics", Operation = "lyrics", Title = "Fixture" });
        Assert.Equal(kind == ProviderErrorKind.NotFound ? "not_found" : error.Code, result.ReasonCode);
        Assert.Equal(error.SafeMessage, result.Message);
    }

    [Fact]
    public async Task LyricsLookup_PreservesSpecificArtistRequirement()
    {
        var lyrics = new Mock<IProviderLyricsCapability>();
        lyrics.Setup(item => item.FetchLyricsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderLyricsRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderLyricsResult>.Failure(ProviderError.LyricsArtistRequired()));
        var registry = new Mock<IProviderRegistry>();
        registry.Setup(item => item.GetRequiredCapability<IProviderLyricsCapability>("fixture", ProviderCapabilityKind.Lyrics)).Returns(lyrics.Object);
        var result = await new ProviderTryOperations(registry.Object).ExecuteAsync(Context(),
            new() { Capability = "lyrics", Operation = "lyrics", Title = "Fixture" });
        Assert.False(result.Succeeded);
        Assert.Equal("needs_artist", result.ReasonCode);
        Assert.Equal("This source needs the artist too", result.Message);
    }
}
