using allstarr.Core.Capabilities;
using allstarr.Core.Configuration;
using allstarr.Core.Jobs;
using allstarr.Core.Playlists;
using allstarr.Core.Playlists.Targets;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class PlaylistOrchestrationIntegrationTests
{
    private readonly Guid _spotifyArtworkAccount = Guid.NewGuid();

    [Fact]
    public async Task Artwork_repair_is_queued_once_and_runs_without_collecting_membership_or_waiting_for_a_schedule()
    {
        await SetSpotifyArtworkLinkAsync(false);
        await using (var setup = await _factory.CreateDbContextAsync())
        {
            (await setup.PlaylistLinks.SingleAsync()).TargetPlaylistId = "native-playlist";
            setup.PlaylistSourceSnapshots.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = _user,
                ProviderAccountId = _spotifyArtworkAccount,
                PlaylistLinkId = _link,
                SnapshotVersion = 1,
                ProviderRevision = "old",
                Name = "Release Radar",
                PayloadSha256 = Hash("old"),
                RetrievedAt = _now,
                CorrelationId = "old"
            });
            await setup.SaveChangesAsync();
        }
        _source.Snapshot = Snapshot("new-cover") with
        {
            ProviderId = "spotify",
            ProviderAccountId = _spotifyArtworkAccount,
            Name = "Release Radar",
            SourceOwnerId = "spotify"
        };
        _target.CanWriteArtwork = true;
        _source.Artwork = ProviderOutcome<ProviderPlaylistArtwork>.Success(new([7, 8, 9], "image/jpeg"));

        var options = new DurableJobOptions();
        var queue = new DurableJobQueue(_factory, options, new JobPayloadPolicy(options), new Clock(_now));
        var old = await queue.EnqueueAsync(new DurableJobEnqueueRequest<PlaylistMaterializationJobPayload>(
            "playlist.materialize", "spotify-artwork-repair:v2:" + _link, new(_link, 1, ArtworkOnly: true), _user,
            ProviderAccountId: _spotifyArtworkAccount, Capability: "playlist"));
        await using (var previous = await _factory.CreateDbContextAsync())
        {
            var failed = await previous.Jobs.SingleAsync(item => item.Id == old.JobId);
            failed.State = DurableJobState.Failed;
            await previous.SaveChangesAsync();
        }
        var scheduler = new DurableScheduleEngine(_factory, queue, new Clock(_now), ReleaseComposition.Development,
            playlists: _service);
        await scheduler.TickAsync();
        await scheduler.TickAsync();
        await using var jobs = await _factory.CreateDbContextAsync();
        Assert.Equal(2, await jobs.Jobs.CountAsync());
        var job = Assert.Single(await jobs.Jobs.Where(item => item.IdempotencyKey.StartsWith("spotify-artwork-repair:v3:")).ToListAsync());
        var payload = System.Text.Json.JsonSerializer.Deserialize<PlaylistMaterializationJobPayload>(job.PayloadJson)!;
        Assert.True(payload.ArtworkOnly);
        var claim = Assert.IsType<DurableJobClaim>(await queue.ClaimNextAsync("cover-repair"));
        var completion = await new PlaylistMaterializationJobHandler(_factory, _service, new Clock(_now))
            .ExecuteAsync(new(claim, EmptyServices.Instance), default);

        Assert.Equal(DurableJobCompletionKind.Succeeded, completion.Kind);
        Assert.Equal([7, 8, 9], _target.ArtworkOnlyBytes);
        Assert.Equal(0, _target.WriteCalls);
        Assert.Equal(0, _source.CollectCalls);
        Assert.Equal(1, _source.SummaryCalls);
        Assert.Empty(await jobs.TrackMatches.ToListAsync());
        Assert.Single(await jobs.PlaylistSourceSnapshots.ToListAsync());
    }

    [Theory]
    [InlineData("unknown-owner")]
    [InlineData(null)]
    public async Task Unknown_source_owner_does_not_permanently_mark_or_enable_a_link(string? owner)
    {
        await SetSpotifyArtworkLinkAsync(false);
        _source.Snapshot = Snapshot("unknown-owner") with
        {
            ProviderId = "spotify",
            ProviderAccountId = _spotifyArtworkAccount,
            Name = "Release Radar",
            SourceOwnerId = owner
        };
        await _service.RunAsync(Context(), new(_link, 1405));
        await using var db = await _factory.CreateDbContextAsync();
        Assert.False((await db.PlaylistLinks.SingleAsync()).SyncArtwork);
        Assert.Empty(await db.ExternalMetadataSnapshots.Where(item => item.ResourceKind == "playlist-artwork-policy").ToListAsync());
    }

    [Theory]
    [InlineData("spotify", "Release Radar", true)]
    [InlineData("spotify:user:spotify", "Discover Weekly", true)]
    [InlineData("spotify", "On Repeat", true)]
    [InlineData("spotify", "Daily Mix 2", true)]
    [InlineData("listener", "Release Radar", false)]
    [InlineData("spotify", "Editorial playlist", false)]
    public async Task Existing_personalized_links_receive_artwork_sync_once(string owner, string name, bool enabled)
    {
        await SetSpotifyArtworkLinkAsync(false);
        _source.Snapshot = Snapshot("cover-upgrade") with { ProviderId = "spotify", ProviderAccountId = _spotifyArtworkAccount, Name = name, SourceOwnerId = owner };
        _target.CanWriteArtwork = true;
        _source.Artwork = ProviderOutcome<ProviderPlaylistArtwork>.Success(new([1, 2, 3], "image/jpeg"));

        await _service.RunAsync(Context(), new(_link, 1401));

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(enabled, (await db.PlaylistLinks.SingleAsync()).SyncArtwork);
        Assert.Equal(enabled ? 1 : 0, _source.ArtworkCalls);
        Assert.Equal(enabled, _target.LastWrite!.Metadata.Artwork != null);
        var marker = Assert.Single(await db.ExternalMetadataSnapshots.Where(item => item.ResourceKind == "playlist-artwork-policy").ToListAsync());
        Assert.Equal(_user, marker.OwnerUserId);
        Assert.Equal("backend", marker.BackendInstanceId);
        Assert.Equal("principal", marker.BackendPrincipalId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Checked_links_preserve_a_later_listener_artwork_opt_out(bool initiallyEnabled)
    {
        await SetSpotifyArtworkLinkAsync(initiallyEnabled);
        _source.Snapshot = Snapshot("cover-upgrade") with { ProviderId = "spotify", ProviderAccountId = _spotifyArtworkAccount, Name = "Release Radar", SourceOwnerId = "spotify" };
        await _service.RunAsync(Context(), new(_link, 1402));
        await using (var update = await _factory.CreateDbContextAsync())
        {
            (await update.PlaylistLinks.SingleAsync()).SyncArtwork = false;
            await update.SaveChangesAsync();
        }

        await _service.RunAsync(Context(), new(_link, 1403));

        await using var db = await _factory.CreateDbContextAsync();
        Assert.False((await db.PlaylistLinks.SingleAsync()).SyncArtwork);
        Assert.Single(await db.ExternalMetadataSnapshots.Where(item => item.ResourceKind == "playlist-artwork-policy").ToListAsync());
    }

    [Fact]
    public async Task Artwork_upgrade_keeps_membership_when_the_source_has_no_usable_cover()
    {
        await SetSpotifyArtworkLinkAsync(false);
        _source.Snapshot = Snapshot("cover-unavailable") with
        {
            ProviderId = "spotify",
            ProviderAccountId = _spotifyArtworkAccount,
            Name = "Release Radar",
            SourceOwnerId = "spotify",
            ArtworkReferenceKey = null
        };
        _target.CanWriteArtwork = true;

        var result = await _service.RunAsync(Context(), new(_link, 1404));

        Assert.Equal(PlaylistSyncState.Succeeded, result.State);
        Assert.Null(_target.LastWrite!.Metadata.Artwork);
        Assert.Equal(0, _source.ArtworkCalls);
    }

    [Fact]
    public async Task Failed_artwork_fetch_keeps_the_current_image_and_waits_for_the_short_cache_to_expire()
    {
        await SetSpotifyArtworkLinkAsync(false);
        await using (var setup = await _factory.CreateDbContextAsync())
        {
            (await setup.PlaylistLinks.SingleAsync()).TargetPlaylistId = "native-playlist";
            await setup.SaveChangesAsync();
        }
        _source.Snapshot = Snapshot("cover-unavailable") with
        {
            ProviderId = "spotify",
            ProviderAccountId = _spotifyArtworkAccount,
            Name = "Release Radar",
            SourceOwnerId = "spotify"
        };
        _source.Artwork = ProviderOutcome<ProviderPlaylistArtwork>.Failure(new(ProviderErrorKind.TransientFailure));
        var result = await _service.RepairSpotifyArtworkAsync(Context(), _link);
        Assert.Equal(DurableJobCompletionKind.Retry, result.Kind);
        Assert.True(result.RetryDelay >= TimeSpan.FromMinutes(2));
        Assert.Null(_target.ArtworkOnlyBytes);
        Assert.Equal(0, _target.WriteCalls);
        Assert.Equal(0, _source.CollectCalls);
    }

    private async Task SetSpotifyArtworkLinkAsync(bool enabled)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var link = await db.PlaylistLinks.SingleAsync();
        link.SourceProviderId = "spotify";
        link.ProviderAccountId = _spotifyArtworkAccount;
        link.SyncArtwork = enabled;
        db.ProviderAccounts.Add(new ProviderAccountRecord
        {
            Id = _spotifyArtworkAccount,
            OwnerUserId = _user,
            ProviderId = "spotify",
            DisplayName = "Spotify",
            Enabled = true,
            CreatedAt = _now,
            UpdatedAt = _now
        });
        await db.SaveChangesAsync();
    }
}
