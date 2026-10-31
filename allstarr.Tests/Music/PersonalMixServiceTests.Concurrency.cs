using System.Text.Json;
using allstarr.Core.Music;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class PersonalMixServiceTests
{
    private static readonly TimeSpan CompletionDeadline = TimeSpan.FromSeconds(10);

    private (TaskCompletionSource Entered, TaskCompletionSource Release) BlockSong(string id, bool fail = false, Song? replacement = null)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", id))
            .Returns(async (ProtocolExecutionContext context, string _, string _) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(context.CancellationToken);
                if (fail) throw new InvalidOperationException("Provider unavailable.");
                return replacement ?? songs[id];
            });
        return (entered, release);
    }

    private (TaskCompletionSource Entered, TaskCompletionSource Release) BlockPlayback(string id)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.Setup(item => item.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.Is<Song>(song => song.Id == id)))
            .Returns(async (ProtocolExecutionContext context, Song _) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(context.CancellationToken);
                return true;
            });
        return (entered, release);
    }

    [Fact]
    public async Task BlockedGeneration_ServesStoredMixesAndPreservesListenerChanges()
    {
        await Seed(count: 30, plays: 7);
        await NewPicks();
        publish = true;
        var before = await service.GenerateAsync(owner);
        var repeat = before.Single(item => item.Kind == "on-repeat");
        var discovery = before.Single(item => item.Kind == "discover-weekly");
        var blend = before.Single(item => item.Kind == "daily-1");
        await service.SetInAppsAsync(owner, repeat.Id, true, default);
        var preview = await service.StartAsync(owner, new("song", songs["0"].Id, 25));
        var deleted = await service.SaveCustomAsync(owner, preview.Id, "Delete during refresh");
        var savedPreview = await service.StartAsync(owner, new("song", songs["1"].Id, 25));
        var link = await ExternalLink();
        var virtualization = new PlaylistVirtualizationService(factory, new(factory, access.Object), access.Object, personalMixes: service);
        clock.UtcNow += TimeSpan.FromMinutes(16);
        var block = BlockPlayback(songs["0"].Id);
        var generation = service.GenerateAsync(owner, timeZoneId: "UTC");
        try
        {
            await block.Entered.Task.WaitAsync(CompletionDeadline);
            Assert.Equal(repeat.Generation, (await service.GetAsync(owner, repeat.Id).WaitAsync(CompletionDeadline)).Generation);
            Assert.Equal(repeat.Generation, (await service.ListAsync(owner).WaitAsync(CompletionDeadline)).Single(item => item.Id == repeat.Id).Generation);
            Assert.Contains(await service.PublishedAsync(Context(owner), default).WaitAsync(CompletionDeadline), item => item.LinkId == repeat.Id);
            Assert.Equal(link.Id, (await virtualization.ReadAsync(Context(owner), PlaylistVirtualizationService.CreateProtocolId(link.Id)).WaitAsync(CompletionDeadline))!.LinkId);
            await service.SetInAppsAsync(owner, repeat.Id, false, default).WaitAsync(CompletionDeadline);
            await service.SetInAppsAsync(owner, discovery.Id, true, default).WaitAsync(CompletionDeadline);
            await service.FeedbackAsync(owner, repeat.Id, new(repeat.Songs[0].Song.Id, "notForMe"), default).WaitAsync(CompletionDeadline);
            await taste.PutNudgeAsync(owner, new("artist", repeat.Songs[1].Song.Artist, "never")).WaitAsync(CompletionDeadline);
            await service.ListAsync(owner, "America/New_York").WaitAsync(CompletionDeadline);
            await service.DeleteCustomAsync(owner, deleted.Id).WaitAsync(CompletionDeadline);
            await service.SaveCustomAsync(owner, savedPreview.Id, "Saved during refresh").WaitAsync(CompletionDeadline);
        }
        finally { block.Release.TrySetResult(); await generation.WaitAsync(CompletionDeadline); }
        var after = await service.ListAsync(owner);
        var refreshed = after.Single(item => item.Id == repeat.Id);
        Assert.Equal(repeat.Generation + 1, refreshed.Generation);
        Assert.False(refreshed.Added);
        Assert.True(after.Single(item => item.Id == discovery.Id).Added);
        Assert.DoesNotContain(refreshed.Songs, item => item.Song.Id == repeat.Songs[0].Song.Id || item.Song.Artist == repeat.Songs[1].Song.Artist);
        Assert.Equal(blend.CoverTag, after.Single(item => item.Id == blend.Id).CoverTag);
        Assert.Equal(blend.Name, after.Single(item => item.Id == blend.Id).Name);
        Assert.DoesNotContain(after, item => item.Id == deleted.Id);
        Assert.Equal("Saved during refresh", after.Single(item => item.Id == savedPreview.Id).Name);
        await using var db = factory.CreateDbContext();
        var row = await db.ExternalMetadataSnapshots.Where(item => item.ResourceKind == "personal-mixes" && item.OwnerUserId == owner)
            .OrderByDescending(item => item.SnapshotVersion).FirstAsync();
        Assert.Equal("America/New_York", JsonDocument.Parse(row.PayloadJson).RootElement.GetProperty("TimeZoneId").GetString());
    }

    [Fact]
    public async Task FeedbackDuringGeneration_ExcludesFetchedCanonicalAlias()
    {
        await Seed(count: 30, plays: 7);
        var repeat = (await service.GenerateAsync(owner)).Single(item => item.Kind == "on-repeat");
        clock.UtcNow += TimeSpan.FromMinutes(16);
        var alias = new Song
        {
            Id = "ext-qobuz-song-alias",
            ExternalProvider = "qobuz",
            ExternalId = "alias",
            CanonicalRecordingId = songs["0"].CanonicalRecordingId,
            Title = songs["0"].Title,
            Artist = songs["0"].Artist,
            ExplicitContentLyrics = 0
        };
        var block = BlockSong("0", replacement: alias);
        var generation = service.GenerateAsync(owner, repeat.Id);
        try
        {
            await block.Entered.Task.WaitAsync(CompletionDeadline);
            await service.FeedbackAsync(owner, repeat.Id, new(songs["0"].Id, "notForMe"), default).WaitAsync(CompletionDeadline);
        }
        finally { block.Release.TrySetResult(); await generation.WaitAsync(CompletionDeadline); }
        var result = (await generation).Single(item => item.Id == repeat.Id);
        Assert.Equal(repeat.Generation + 1, result.Generation);
        Assert.DoesNotContain(result.Songs, item => item.Song.CanonicalRecordingId == alias.CanonicalRecordingId);
        Assert.Equal(29, result.Songs.Count);
    }

    [Fact]
    public async Task SameTimezonePolling_DoesNotAppendSnapshotsOrConflictWithPublication()
    {
        await Seed();
        var repeat = (await service.GenerateAsync(owner)).Single(item => item.Kind == "on-repeat");
        await service.ListAsync(owner, "UTC");
        await using var db = factory.CreateDbContext();
        var before = await db.ExternalMetadataSnapshots.CountAsync(item => item.ResourceKind == "personal-mixes");
        clock.UtcNow += TimeSpan.FromMinutes(16);
        var polls = 0;
        access.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns(async (ProtocolExecutionContext _, CancellationToken token) =>
            {
                await service.ListAsync(owner, "UTC", token).WaitAsync(CompletionDeadline);
                await service.ListAsync(owner, "UTC", token).WaitAsync(CompletionDeadline);
                Interlocked.Increment(ref polls);
                return new BackendLibraryAccess(true, []);
            });
        var result = (await service.GenerateAsync(owner, repeat.Id, timeZoneId: "UTC").WaitAsync(CompletionDeadline)).Single(item => item.Id == repeat.Id);
        Assert.Equal(2, polls);
        Assert.Equal(repeat.Generation + 1, result.Generation);
        Assert.Equal(before + 1, await db.ExternalMetadataSnapshots.CountAsync(item => item.ResourceKind == "personal-mixes"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlockedCustomPreview_DoesNotBlockSavedMixAccessOnCancellationOrFailure(bool fail)
    {
        await Seed();
        var repeat = (await service.GenerateAsync(owner)).Single(item => item.Kind == "on-repeat");
        using var cancellation = new CancellationTokenSource();
        var block = BlockSong("0", fail);
        var preview = service.StartAsync(owner, new("song", songs["0"].Id), cancellation.Token);
        try
        {
            await block.Entered.Task.WaitAsync(CompletionDeadline);
            Assert.Equal(repeat.Generation, (await service.GetAsync(owner, repeat.Id).WaitAsync(CompletionDeadline)).Generation);
            await service.SetInAppsAsync(owner, repeat.Id, true, default).WaitAsync(CompletionDeadline);
        }
        finally
        {
            if (!fail) cancellation.Cancel();
            block.Release.TrySetResult();
            if (fail) await Assert.ThrowsAsync<InvalidOperationException>(() => preview.WaitAsync(CompletionDeadline));
            else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preview.WaitAsync(CompletionDeadline));
        }
        Assert.True((await service.GetAsync(owner, repeat.Id)).Added);
        Assert.Equal(repeat.Songs.Count, (await service.GetAsync(owner, repeat.Id)).Songs.Count);
    }

    [Fact]
    public async Task CustomSave_ValidatesOutsideDocumentWritesAndKeepsNewChoices()
    {
        await Seed();
        var repeat = (await service.GenerateAsync(owner)).Single(item => item.Kind == "on-repeat");
        gateway.Setup(item => item.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "0", It.IsAny<int>()))
            .ReturnsAsync(songs.Values.ToList());
        var preview = await service.StartAsync(owner, new("song", songs["0"].Id, 25));
        var block = BlockPlayback(preview.Songs[0].Song.Id);
        var save = service.SaveCustomAsync(owner, preview.Id, "Saved");
        try
        {
            await block.Entered.Task.WaitAsync(CompletionDeadline);
            await service.SetInAppsAsync(owner, repeat.Id, true, default).WaitAsync(CompletionDeadline);
            await taste.PutNudgeAsync(owner, new("artist", preview.Songs[0].Song.Artist, "never")).WaitAsync(CompletionDeadline);
            Assert.Equal(repeat.Generation, (await service.GetAsync(owner, repeat.Id).WaitAsync(CompletionDeadline)).Generation);
        }
        finally { block.Release.TrySetResult(); await save.WaitAsync(CompletionDeadline); }
        Assert.DoesNotContain((await save).Songs, item => item.Song.Id == preview.Songs[0].Song.Id);
        Assert.True((await service.GetAsync(owner, repeat.Id)).Added);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.GetAsync(other, preview.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedImportedInputs_LeaveStoredGenerationIntact(bool delete)
    {
        await Seed();
        var repeat = (await service.GenerateAsync(owner)).Single(item => item.Kind == "on-repeat");
        clock.UtcNow += TimeSpan.FromMinutes(16);
        var block = BlockSong("0");
        var generation = service.GenerateAsync(owner, repeat.Id);
        try
        {
            await block.Entered.Task.WaitAsync(CompletionDeadline);
            if (delete)
            {
                await using var db = factory.CreateDbContext();
                await db.ListeningEvents.Where(item => item.OwnerUserId == owner && item.SourceKind == "import").ExecuteDeleteAsync();
            }
            else await taste.SetUseImportedHistoryAsync(owner, false).WaitAsync(CompletionDeadline);
        }
        finally { block.Release.TrySetResult(); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => generation.WaitAsync(CompletionDeadline));
        var stored = await service.GetAsync(owner, repeat.Id);
        Assert.Equal(repeat.Generation, stored.Generation);
        Assert.Equal(repeat.Songs.Select(item => item.Song.Id), stored.Songs.Select(item => item.Song.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedGenerationProviders_LeaveStoredContentUsable(bool playback)
    {
        await Seed();
        var repeat = (await service.GenerateAsync(owner)).Single(item => item.Kind == "on-repeat");
        clock.UtcNow += TimeSpan.FromMinutes(16);
        if (playback)
            gateway.Setup(item => item.CanPlaySongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<Song>()))
                .ThrowsAsync(new InvalidOperationException("Upstream detail."));
        else
            gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>()))
                .ThrowsAsync(new InvalidOperationException("Upstream detail."));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateAsync(owner, repeat.Id));
        Assert.Equal("Mix sources are unavailable. Try again later.", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("Upstream detail", error.ToString(), StringComparison.Ordinal);
        var stored = await service.GetAsync(owner, repeat.Id);
        Assert.Equal(repeat.Generation, stored.Generation);
        Assert.Equal(repeat.Songs.Select(item => item.Song.Id), stored.Songs.Select(item => item.Song.Id));
    }

    [Fact]
    public async Task Publication_RebasesListenerChoicesWithoutFetchingProvidersAgain()
    {
        await Seed(count: 30, plays: 7);
        var repeat = (await service.GenerateAsync(owner)).Single(item => item.Kind == "on-repeat");
        songs["1"].ExplicitContentLyrics = 1;
        clock.UtcNow += TimeSpan.FromMinutes(16);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var validations = 0;
        access.Setup(item => item.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .Returns(async (ProtocolExecutionContext _, CancellationToken token) =>
            {
                if (Interlocked.Increment(ref validations) == 2)
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(token);
                }
                return new BackendLibraryAccess(true, []);
            });
        var generation = service.GenerateAsync(owner, repeat.Id);
        var calls = 0;
        try
        {
            await entered.Task.WaitAsync(CompletionDeadline);
            calls = gateway.Invocations.Count;
            await service.SetInAppsAsync(owner, repeat.Id, true, default).WaitAsync(CompletionDeadline);
            await service.FeedbackAsync(owner, repeat.Id, new(songs["0"].Id, "notForMe"), default).WaitAsync(CompletionDeadline);
            await taste.PutNudgeAsync(owner, new("artist", "Artist 2", "never")).WaitAsync(CompletionDeadline);
            filter = "CleanOnly";
        }
        finally { release.TrySetResult(); await generation.WaitAsync(CompletionDeadline); }
        Assert.Equal(calls, gateway.Invocations.Count);
        Assert.Equal(3, validations);
        var result = (await generation).Single(item => item.Id == repeat.Id);
        Assert.True(result.Added);
        Assert.Equal(repeat.Generation + 1, result.Generation);
        Assert.DoesNotContain(result.Songs, item => item.Song.ExternalId is "0" or "1" or "2");
    }

    [Fact]
    public async Task SlowLinkedProjection_DoesNotHoldDocumentWrites()
    {
        await Seed();
        var repeat = (await service.GenerateAsync(owner)).Single(item => item.Kind == "on-repeat");
        await ExternalLink();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        access.Setup(item => item.ResolveUserAsync(owner, It.IsAny<CancellationToken>()))
            .Returns(async (Guid _, CancellationToken token) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return new BackendLibraryAccessContext(Context(owner), new(true, []));
            });
        var listing = service.ListAsync(owner);
        try
        {
            await entered.Task.WaitAsync(CompletionDeadline);
            await service.SetInAppsAsync(owner, repeat.Id, true, default).WaitAsync(CompletionDeadline);
            Assert.Equal(repeat.Generation, (await service.GetAsync(owner, repeat.Id).WaitAsync(CompletionDeadline)).Generation);
        }
        finally { release.TrySetResult(); await listing.WaitAsync(CompletionDeadline); }
        Assert.True((await service.GetAsync(owner, repeat.Id)).Added);
    }

    [Fact]
    public async Task RejectedRefresh_DoesNotChangeSavedTimezone()
    {
        await Seed();
        var repeat = (await service.GenerateAsync(owner)).Single(item => item.Kind == "on-repeat");
        await service.ListAsync(owner, "UTC");
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.GenerateAsync(owner, Guid.NewGuid(), timeZoneId: "America/New_York"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateAsync(owner, repeat.Id, timeZoneId: "America/New_York"));
        await using var db = factory.CreateDbContext();
        var row = await db.ExternalMetadataSnapshots.Where(item => item.ResourceKind == "personal-mixes" && item.OwnerUserId == owner)
            .OrderByDescending(item => item.SnapshotVersion).FirstAsync();
        Assert.Equal("UTC", JsonDocument.Parse(row.PayloadJson).RootElement.GetProperty("TimeZoneId").GetString());
    }

    [Fact]
    public async Task Generations_RemainSerializedAndCancellationKeepsStoredContent()
    {
        await Seed();
        var repeat = (await service.GenerateAsync(owner)).Single(item => item.Kind == "on-repeat");
        clock.UtcNow += TimeSpan.FromMinutes(16);
        using var cancellation = new CancellationTokenSource();
        var block = BlockSong("0");
        gateway.Invocations.Clear();
        var first = service.GenerateAsync(owner, token: cancellation.Token);
        Task<IReadOnlyList<PersonalMix>>? second = null;
        try
        {
            await block.Entered.Task.WaitAsync(CompletionDeadline);
            second = service.GenerateAsync(owner, repeat.Id);
            Assert.False(second.IsCompleted);
            gateway.Verify(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "0"), Times.Once);
            Assert.Equal(repeat.Generation, (await service.GetAsync(owner, repeat.Id).WaitAsync(CompletionDeadline)).Generation);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(CompletionDeadline));
            Assert.Equal(repeat.Generation, (await service.GetAsync(owner, repeat.Id).WaitAsync(CompletionDeadline)).Generation);
        }
        finally
        {
            cancellation.Cancel();
            block.Release.TrySetResult();
            try { await first.WaitAsync(CompletionDeadline); } catch (OperationCanceledException) { }
            if (second != null) await second.WaitAsync(CompletionDeadline);
        }
        Assert.Equal(repeat.Generation + 1, (await service.GetAsync(owner, repeat.Id)).Generation);
    }
}
