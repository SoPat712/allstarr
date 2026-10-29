using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Playlists;
using allstarr.Core.Playlists.Sources;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class PlaylistOrchestrationIntegrationTests
{
    [Fact]
    public async Task LastFmDisconnectedSourceKeepsItsConnectionReasonThroughCollection()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var account = new ProviderAccountRecord
        {
            Id = Guid.CreateVersion7(),
            ProviderId = "lastfm",
            OwnerUserId = _user,
            DisplayName = "Last.fm",
            Enabled = false,
            CreatedAt = _now,
            UpdatedAt = _now
        };
        db.ProviderAccounts.Add(account);
        await db.SaveChangesAsync();
        var link = await db.PlaylistLinks.SingleAsync(item => item.Id == _link);
        link.SourceProviderId = "lastfm";
        link.ProviderAccountId = account.Id;
        var registry = new Mock<IProviderRegistry>(MockBehavior.Strict);
        var gateway = new ProviderPlaylistSourceGateway(registry.Object, new ProviderPlaylistSnapshotCollector(), _factory, new Clock(_now));

        var failure = await Assert.ThrowsAsync<PlaylistSourceUnavailableException>(() => gateway.CollectAsync(Context(), link, default));

        Assert.Equal("lastfm-not-connected", failure.Code);
        registry.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LastFmDisconnectedAccountKeepsItsConnectionReasonDuringRuns()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var account = new ProviderAccountRecord
            {
                Id = Guid.CreateVersion7(),
                ProviderId = "lastfm",
                OwnerUserId = _user,
                DisplayName = "Last.fm",
                Enabled = false,
                CreatedAt = _now,
                UpdatedAt = _now
            };
            db.ProviderAccounts.Add(account);
            var link = await db.PlaylistLinks.SingleAsync(item => item.Id == _link);
            link.SourceProviderId = "lastfm";
            link.ProviderAccountId = account.Id;
            await db.SaveChangesAsync();
        }

        var failure = await Assert.ThrowsAsync<PlaylistSourceUnavailableException>(() =>
            _service.RunAsync(Context(), new(_link, 1)));

        Assert.Equal("lastfm-not-connected", failure.Code);
        Assert.Equal(0, _source.CollectCalls);
    }

    [Fact]
    public async Task SourceFailurePreservesTheSafeReasonAndRetryTiming()
    {
        var error = ProviderError.LastFmApiKeyRequired();
        var capability = new Mock<IProviderPlaylistCapability>();
        capability.SetupGet(item => item.ProviderId).Returns("fixture");
        capability.Setup(item => item.GetPlaylistTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderPlaylistTracksRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPlaylistTrackPage>.Failure(error));
        var registry = new Mock<IProviderRegistry>();
        registry.Setup(item => item.GetRequiredCapability<IProviderPlaylistCapability>("fixture", ProviderCapabilityKind.Playlist))
            .Returns(capability.Object);
        await using var db = await _factory.CreateDbContextAsync();
        var link = await db.PlaylistLinks.SingleAsync(item => item.Id == _link);
        var gateway = new ProviderPlaylistSourceGateway(registry.Object, new ProviderPlaylistSnapshotCollector(), _factory, new Clock(_now));

        var failure = await Assert.ThrowsAsync<PlaylistSourceUnavailableException>(() => gateway.CollectAsync(Context(), link, default));

        Assert.Equal("lastfm-api-key-required", failure.Code);
        Assert.Equal("Needs Last.fm API key.", failure.Message);
        var limited = new PlaylistSourceUnavailableException("source_unavailable", new(ProviderErrorKind.RateLimited, TimeSpan.FromSeconds(17)));
        Assert.Equal(TimeSpan.FromSeconds(17), limited.Error!.RetryAfter);
    }

    [Fact]
    public async Task SourceMbidIsRetainedAsMetadataWithoutOverridingTitleAndArtistMatching()
    {
        const string mbid = "a6b671a0-6cf0-47a1-aab1-51ce8d12b812";
        var account = await SetPlaylistSourceAccountAsync("lastfm");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var track = await db.LibraryTracks.SingleAsync(item => item.Id == _trackOne);
            track.MusicBrainzRecordingId = mbid;
            await db.SaveChangesAsync();
        }
        _source.Snapshot = Snapshot("supporting-mbid", Entry(0, "entry", "unproven-source", "Unrelated title") with
        {
            Artists = ["Unrelated artist"],
            MusicBrainzRecordingId = mbid
        }) with { ProviderId = "lastfm", ProviderAccountId = account };

        var refreshed = await _service.RefreshAsync(Context(), _link);

        await using var result = await _factory.CreateDbContextAsync();
        var external = await result.ExternalMetadataSnapshots.SingleAsync();
        var metadata = JsonSerializer.Deserialize<PlaylistSourceMetadata>(external.PayloadJson);
        Assert.Equal(mbid, metadata!.MusicBrainzRecordingId);
        Assert.Null(metadata.CanonicalRecordingId);
        var decision = await result.TrackMatches.SingleAsync();
        Assert.False(decision.State is TrackMatchState.Accepted or TrackMatchState.Pinned);
        Assert.Equal(1, refreshed.SnapshotVersion);
    }

    private async Task<Guid> SetPlaylistSourceAccountAsync(string provider)
    {
        var account = Guid.CreateVersion7();
        await using var db = await _factory.CreateDbContextAsync();
        db.ProviderAccounts.Add(new ProviderAccountRecord
        {
            Id = account,
            ProviderId = provider,
            OwnerUserId = _user,
            DisplayName = "Playlist source",
            Enabled = true,
            CreatedAt = _now,
            UpdatedAt = _now
        });
        var link = await db.PlaylistLinks.SingleAsync(item => item.Id == _link);
        link.SourceProviderId = provider;
        link.ProviderAccountId = account;
        await db.SaveChangesAsync();
        return account;
    }
}
