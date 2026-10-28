using allstarr.Core.Music;
using allstarr.Core.Matching;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Services.Common;
using Moq;

namespace allstarr.Tests;

public sealed partial class TrackIdentityServiceTests
{
    [Fact]
    public async Task PartialMeasurementsRetainCompatibleCompleteFileFacts()
    {
        var execution = Context(Actor(_userA), "artistgrid");
        var track = Track("artistgrid", "partial");
        await _service.RememberMediaFactsAsync(execution, track,
            new(new("audio/mp4", "mp4", "alac", 960000, 96000, 24, 2), 12000000, 100,
                new() { TrackGain = -6.5 }));
        await _service.RememberMediaFactsAsync(execution, track, new(new("audio/mp4", "mp4", "unknown"), 12000000));
        var song = new Song { ExternalProvider = "artistgrid", ExternalId = "partial" };
        await _service.ApplyStoredMediaFactsAsync(Actor(_userA), [song]);
        Assert.Equal("alac", song.Codec);
        Assert.Equal(96000, song.SampleRate);
        Assert.Equal(100, song.Duration);
        Assert.Equal(-6.5, song.ReplayGain!.TrackGain);
        await _service.RememberMediaFactsAsync(execution, track, new(new("audio/mpeg", "mp3", "mp3")));
        await _service.ApplyStoredMediaFactsAsync(Actor(_userA), [song]);
        Assert.Equal("mp3", song.Codec);
        Assert.Null(song.SampleRate);
        Assert.Null(song.BitDepth);
        Assert.Null(song.Size);
    }

    [Fact]
    public async Task MeasuredMediaRejectsStaleAccountsAndDoesNotCrossSourceOrViewer()
    {
        var account = await SeedUserAccount("deezer", _userA);
        var execution = Context(Actor(_userA), "deezer", account);
        await _service.RememberMediaFactsAsync(execution, Track("deezer", "same"),
            new(new("audio/flac", "flac", "flac", 960000, 96000, 24, 2), 12000000));
        var own = new Song { ExternalProvider = "deezer", ExternalId = "same" };
        var otherSource = new Song { ExternalProvider = "qobuz", ExternalId = "same" };
        await _service.ApplyStoredMediaFactsAsync(Actor(_userA), [own, otherSource]);
        Assert.Equal(96000, own.SampleRate);
        Assert.Null(otherSource.SampleRate);
        await using var db = await _factory.CreateDbContextAsync();
        (await db.ProviderAccounts.SingleAsync(item => item.Id == account.Id)).Revision++;
        await db.SaveChangesAsync();
        var stale = new Song { ExternalProvider = "deezer", ExternalId = "same" };
        await _service.ApplyStoredMediaFactsAsync(Actor(_userA), [stale]);
        Assert.Null(stale.SampleRate);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.RememberMediaFactsAsync(execution,
            Track("deezer", "same"), new(new("audio/flac", "flac", "flac"), 12000000)));
    }

    [Fact]
    public async Task StoredTypedMetadataRetainsCreditsRightsAndTechnicalFacts()
    {
        var metadata = new ProviderTrackMetadata(Track("artistgrid", "stored"), "Stored", [new("Artist")],
            albumTitle: "Era", duration: TimeSpan.FromSeconds(115.2), releaseDate: "2020-07-17",
            totalTracks: 12, totalDiscs: 2, upc: "123456789012", composer: "Composer", label: "Label",
            copyright: "2020 Label", contributors: ["producer: Producer"], notes: "Demo", genres: ["Pop"],
            media: new("audio/wav", "wav", "pcm_s24le", 2116800, 44100, 24, 2), size: 30645098,
            replayGain: new() { TrackGain = 0 });
        var payload = System.Text.Json.JsonSerializer.Serialize(metadata);
        await using var db = await _factory.CreateDbContextAsync();
        db.ExternalMetadataSnapshots.Add(new()
        {
            Id = Guid.NewGuid(),
            OwnerUserId = _userA,
            ProviderId = "artistgrid",
            ResourceKind = "track",
            BackendInstanceId = "fixture",
            BackendPrincipalId = _userA.ToString("N"),
            Protocol = "jellyfin",
            ExternalIdHash = allstarr.Core.Matching.CanonicalCatalogKeys.Hash("stored"),
            SnapshotVersion = 1,
            ProviderRevision = "1",
            PayloadJson = payload,
            PayloadSha256 = allstarr.Core.Matching.CanonicalCatalogKeys.Hash(payload),
            CorrelationId = "stored-metadata",
            RetrievedAt = _clock.UtcNow
        });
        await db.SaveChangesAsync();
        var song = Assert.Single(await _service.GetStoredSongsAsync(Actor(_userA), "ext-artistgrid-song-stored"));
        Assert.Equal("123456789012", song.Upc);
        Assert.Equal("Composer", song.Composer);
        Assert.Equal("Label", song.Label);
        Assert.Equal("2020 Label", song.Copyright);
        Assert.Equal("Demo", song.Notes);
        Assert.Equal(new[] { "producer: Producer" }, song.Contributors);
        Assert.Equal(44100, song.SampleRate);
        Assert.Equal(0d, song.ReplayGain!.TrackGain);
        Assert.Equal(115, song.Duration);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    [InlineData(4096)]
    public async Task StreamingOnceRetainsMeasuredFormatForLaterDetailWithoutProviderCalls(int chunkSize)
    {
        var bytes = AudioMediaFactsTests.Wave();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), 36);
        var opened = 0;
        var capability = new Mock<IProviderStreamingCapability>(MockBehavior.Strict);
        capability.SetupGet(item => item.ProviderId).Returns("artistgrid");
        capability.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Streaming);
        capability.Setup(item => item.GetStreamLeaseAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderStreamLeaseRequest>()))
            .ReturnsAsync(new ProviderOutcomeFactory().Lease(() =>
            {
                opened++;
                var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                { Content = new StreamContent(new ChunkedMediaStream(bytes, chunkSize)) };
                response.Content.Headers.ContentType = new("audio/wav");
                return response;
            }));
        var metadata = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        metadata.SetupGet(item => item.ProviderId).Returns("artistgrid");
        metadata.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Metadata);
        metadata.Setup(item => item.GetTrackAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderTrackLookupRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderTrackMetadata>.Success(new(new("artistgrid", ProviderResourceKind.Track, "wave"), "Wave", [new("Artist")])));
        var streamDescriptor = new ProviderCapabilityDescriptor(ProviderCapabilityKind.Streaming, ProviderCapabilitySupportState.Supported,
            ProviderAccountRequirement.None, "1", ["getStreamLease", "probeStream"]);
        var metadataDescriptor = new ProviderCapabilityDescriptor(ProviderCapabilityKind.Metadata, ProviderCapabilitySupportState.Supported,
            ProviderAccountRequirement.None, "1", ["searchTracks", "getTrack"]);
        var descriptor = new ProviderDescriptor("artistgrid", "ArtistGrid", "Fixture", ProviderOrigin.BuiltIn, "1", "1",
            [streamDescriptor, metadataDescriptor], new ProviderPermissionDescriptor());
        var registry = new ProviderRegistry([new ProviderRegistration(descriptor, [capability.Object, metadata.Object])]);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        router.Setup(item => item.PlanAsync<IProviderStreamingCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => new ProviderRoutePlan<IProviderStreamingCapability>(request,
                [new(0, descriptor, streamDescriptor, capability.Object, Context(Actor(_userA), "artistgrid"), Track("artistgrid", "wave"))],
                new(request.CorrelationId, ProviderCapabilityKind.Streaming, "artistgrid", null, [])));
        router.Setup(item => item.PlanAsync<IProviderMetadataCapability>(It.IsAny<ProviderRouteRequest>()))
            .ReturnsAsync((ProviderRouteRequest request) => new ProviderRoutePlan<IProviderMetadataCapability>(request,
                [new(0, descriptor, metadataDescriptor, metadata.Object, Context(Actor(_userA), "artistgrid"), Track("artistgrid", "wave"))],
                new(request.CorrelationId, ProviderCapabilityKind.Metadata, "artistgrid", null, [])));
        using var cache = new MemoryApplicationCache(_clock);
        var gateway = new ProtocolProviderGateway(router.Object, registry, Mock.Of<IHttpClientFactory>(MockBehavior.Strict),
            identities: _service, applicationCache: cache, audioVerifier: FixtureAudioVerifier.Instance);
        var protocol = DisplayProtocol(ProtocolKind.Jellyfin);
        Assert.Null((await gateway.GetSongAsync(protocol, "artistgrid", "wave"))!.SampleRate);
        var stream = await gateway.OpenStreamAsync(protocol, "artistgrid", "wave", ProviderAudioQuality.Any, null);
        Assert.NotNull(stream);
        using (stream.Response)
        {
            Assert.Equal("wav", stream.Lease.Media.Container);
            Assert.Equal("pcm_s24le", stream.Lease.Media.Codec);
            Assert.Equal(bytes, await stream.Response.Content.ReadAsByteArrayAsync());
        }
        await using var db = await _factory.CreateDbContextAsync();
        var snapshots = await db.ExternalMetadataSnapshots.CountAsync();
        var later = await gateway.GetSongAsync(protocol, "artistgrid", "wave");
        Assert.Equal(44100, later!.SampleRate);
        Assert.Equal(24, later.BitDepth);
        Assert.Equal(2116800, later.Bitrate);
        Assert.Equal(bytes.Length, later.Size);
        Assert.Equal("wav", later.Container);
        Assert.Equal(1, opened);
        metadata.Verify(item => item.GetTrackAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderTrackLookupRequest>()), Times.Once);
        Assert.Equal(snapshots, await db.ExternalMetadataSnapshots.CountAsync());
        var restarted = new ProtocolProviderGateway(Mock.Of<IProviderRouter>(MockBehavior.Strict), registry,
            Mock.Of<IHttpClientFactory>(MockBehavior.Strict), identities: new TrackIdentityService(_factory, _storageState, _clock),
            audioVerifier: FixtureAudioVerifier.Instance);
        var retained = await restarted.GetStoredSongAsync(protocol, "artistgrid", "wave");
        Assert.Equal("Wave", retained!.Title);
        Assert.Equal("Artist", retained.Artist);
        Assert.Equal(44100, retained.SampleRate);
        var inspected = await gateway.OpenStreamAsync(protocol with { IsReadOnly = true }, "artistgrid", "wave", ProviderAudioQuality.Any, null, true);
        Assert.NotNull(inspected);
        using (inspected.Response)
        {
            Assert.Equal("wav", inspected.Lease.Media.Container);
            Assert.Equal("pcm_s24le", inspected.Lease.Media.Codec);
            Assert.Equal(44100, inspected.Lease.Media.SampleRate);
        }
        Assert.Equal(snapshots, await db.ExternalMetadataSnapshots.CountAsync());
    }

    private sealed class ProviderOutcomeFactory
    {
        public ProviderOutcome<ProviderStreamLease> Lease(Func<HttpResponseMessage> open) => ProviderOutcome<ProviderStreamLease>.Success(new(
            "wave", new Uri("https://media.example.test/wave"), DateTimeOffset.UtcNow.AddMinutes(5), true, true,
            new("audio/flac", "flac", "flac"), ProviderStreamRetryBehavior.DoNotRetry, (_, _) => Task.FromResult(open())));
    }

    private sealed class ChunkedMediaStream(byte[] bytes, int chunkSize) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], cancellationToken);
    }

    [Fact]
    public async Task MeasuredMediaSurvivesRestartAndReadsWithoutWritesOrProviderCalls()
    {
        var execution = Context(Actor(_userA), "artistgrid");
        var track = Track("artistgrid", "wave");
        var facts = new TrackMediaFacts(new("audio/wav", "wav", "pcm_s24le", 2116800, 44100, 24, 2), 30645098, 115.2);
        await _service.RememberMediaFactsAsync(execution, track, facts);
        await _service.RememberMediaFactsAsync(execution, track, facts);
        var restarted = new allstarr.Core.Matching.TrackIdentityService(_factory, _storageState, _clock);
        var song = new Song { ExternalProvider = "artistgrid", ExternalId = "wave", Codec = "flac", Container = "flac" };
        await using var db = await _factory.CreateDbContextAsync();
        var count = await db.ExternalMetadataSnapshots.CountAsync();
        Assert.Equal(1, count);
        await restarted.ApplyStoredMediaFactsAsync(Actor(_userA), [song]);
        Assert.Equal("wav", song.Container);
        Assert.Equal("pcm_s24le", song.Codec);
        Assert.Equal(30645098, song.Size);
        Assert.Equal(44100, song.SampleRate);
        Assert.Equal(2116800, song.Bitrate);
        Assert.Equal(115.2, song.PreciseDuration!.Value.TotalSeconds, 3);
        Assert.Equal(count, await db.ExternalMetadataSnapshots.CountAsync());
        var other = new Song { ExternalProvider = "artistgrid", ExternalId = "wave" };
        await restarted.ApplyStoredMediaFactsAsync(Actor(_userB), [other]);
        Assert.Null(other.Size);
        Assert.Empty(await db.CanonicalRecordings.ToArrayAsync());
    }
}
