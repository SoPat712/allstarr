using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Services.Common;
using Moq;

namespace allstarr.Tests;

public sealed class ProviderTryOperationsTests
{
    [Fact]
    public void Catalog_UsesOnlyDeclaredHooksAndSelectedAccounts()
    {
        var descriptor = Descriptor(new ProviderCapabilityDescriptor(ProviderCapabilityKind.Metadata, ProviderCapabilitySupportState.Supported,
            ProviderAccountRequirement.None, "1", ["searchTracks", "searchArtists", "getTrack"]),
            new(ProviderCapabilityKind.Playlist, ProviderCapabilitySupportState.Supported,
                ProviderAccountRequirement.Required, "1", ["getUserPlaylists"], [allstarr.Core.Storage.ProviderAccountScope.Personal]));
        var catalog = ProviderTryOperations.Describe(descriptor, null);
        var metadata = catalog.Capabilities.Single(item => item.Capability == "metadata");
        Assert.Equal(["search", "track"], metadata.Operations.Select(item => item.Id));
        Assert.Equal(["tracks", "artists"], metadata.Operations[0].Kinds);
        Assert.Equal("mine", catalog.Capabilities.Single(item => item.Capability == "playlist").Operations.Single().Id);
    }

    [Theory]
    [InlineData("search", null, null, null, "A search query is required.")]
    [InlineData("search", "query", "songs", null, "Search kind must be tracks, albums or artists.")]
    [InlineData("stream", null, null, "best", "Quality must be any, lossy, lossless or high-resolution.")]
    [InlineData("track", null, null, null, "A track id is required.")]
    public void Validation_ReturnsPlainSentences(string operation, string? query, string? kind, string? quality, string expected)
    {
        Assert.Equal(expected, new ProviderTryRequest { Operation = operation, Query = query, Kind = kind, Quality = quality }.Validate());
    }

    [Fact]
    public void Validation_RejectsLongText()
    {
        Assert.Equal("Text inputs must be at most 500 characters.",
            new ProviderTryRequest { Operation = "lyrics", Title = new string('x', 501) }.Validate());
    }

    [Theory]
    [InlineData("success", true, null)]
    [InlineData("empty", false, "not_found")]
    [InlineData("error", false, "transient-failure")]
    [InlineData("exception", false, "provider_error")]
    [InlineData("timeout", false, "timeout")]
    public async Task Search_ProjectsBoundedResultsAndSanitizesFailures(string mode, bool succeeded, string? reason)
    {
        var metadata = new Mock<IProviderMetadataCapability>();
        metadata.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
            .Returns((ProviderExecutionContext _, ProviderMetadataSearchRequest request) =>
            {
                Assert.Equal(20, request.Page.Limit);
                if (mode == "exception") throw new InvalidOperationException("https://private.test?token=secret");
                if (mode == "timeout") throw new OperationCanceledException();
                return Task.FromResult(mode == "error"
                    ? ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Failure(new(ProviderErrorKind.TransientFailure))
                    : ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("fixture", mode == "empty" ? [] :
                        Enumerable.Range(0, 25).Select(index => new ProviderTrackMetadata(new("fixture", ProviderResourceKind.Track, index.ToString()),
                            "Song", [new("Artist")])))));
            });
        var registry = new Mock<IProviderRegistry>();
        registry.Setup(item => item.GetRequiredCapability<IProviderMetadataCapability>("fixture", ProviderCapabilityKind.Metadata)).Returns(metadata.Object);
        var operations = new ProviderTryOperations(registry.Object);
        var context = Context();
        var result = await operations.ExecuteAsync(context, new ProviderTryRequest { Capability = "metadata", Operation = "search", Query = "sample", Kind = "tracks" });
        Assert.Equal(succeeded, result.Succeeded);
        Assert.Equal(reason, result.ReasonCode);
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("secret", json);
        Assert.True(JsonDocument.Parse(json).RootElement.TryGetProperty("message", out _));
        if (succeeded) Assert.Equal(20, result.Tracks!.Count);
    }

    [Fact]
    public async Task ExtensionMetadataProbe_SearchFailureIsDegraded()
    {
        var metadata = new Mock<IProviderMetadataCapability>();
        metadata.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(),
                It.Is<ProviderMetadataSearchRequest>(request => request.Query == "Daft Punk Get Lucky")))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Failure(new(ProviderErrorKind.TransientFailure)));
        var registry = new Mock<IProviderRegistry>();
        registry.Setup(item => item.GetRequiredCapability<IProviderMetadataCapability>("fixture", ProviderCapabilityKind.Metadata)).Returns(metadata.Object);
        var result = await new ProviderTryOperations(registry.Object).ProbeAsync(Context(),
            Descriptor(new ProviderCapabilityDescriptor(ProviderCapabilityKind.Metadata, ProviderCapabilitySupportState.Supported,
                ProviderAccountRequirement.None, "1", ["searchTracks"])), ProviderCapabilityKind.Metadata);
        Assert.False(result.Succeeded);
        metadata.VerifyAll();
    }

    internal static ProviderExecutionContext Context() => new(
        new ProviderActorContext(ProviderActorKind.SystemJob, null, durableJobId: Guid.NewGuid()), "fixture", null,
        new ProviderExecutionPolicy(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true),
            ProviderExplicitContentPolicy.Allow, false, true, true, ["fixture"]), "provider-try", "test",
        DateTimeOffset.UtcNow.AddSeconds(30), CancellationToken.None);

    internal static ProviderDescriptor Descriptor(params ProviderCapabilityDescriptor[] capabilities) => new(
        "fixture", "Fixture", "Fixture provider", ProviderOrigin.Extension, "1", "1", capabilities,
        new ProviderPermissionDescriptor(), entryPoint: "main.js");
}
