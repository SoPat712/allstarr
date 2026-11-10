using System.Text.Json;
using allstarr.Core.Music;
using allstarr.Core.Capabilities;
using Moq;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class PersonalMixServiceTests
{
    [Fact]
    public async Task ServiceFeedCoverVersionsAreWarmedAfterRefresh()
    {
        var first = new TaskCompletionSource<MixCoverInput>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<MixCoverInput>(TaskCreationOptions.RunContinuationsAsynchronously);
        var another = new TaskCompletionSource<MixCoverInput>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var covers = new MixCoverImageService(_ => throw new InvalidOperationException("Unexpected gradient"),
            serviceArt: (input, listener, _) =>
            {
                Assert.Equal(owner, listener);
                Interlocked.Increment(ref calls);
                if (input.ServiceArtwork?.Contains("cover-revision2", StringComparison.Ordinal) == true) second.TrySetResult(input);
                else if (input.SourcePlaylistId == "37i9dQZweekly") first.TrySetResult(input);
                else another.TrySetResult(input);
                return Task.FromResult<ResolvedMediaAsset?>(new([1, 2, 3], "image/png", "hash", null, null, false));
            }, cache: new TestMemoryApplicationCache());
        var (capability, _) = await FeedAccount(covers: covers);
        capability.Setup(item => item.GetUserPlaylistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderUserPlaylistsRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Success(new("spotify",
                [new(new("spotify", ProviderResourceKind.Playlist, "37i9dQZweekly"), "Discover Weekly", new("spotify"), "revision",
                    artwork: new(new("spotify", ProviderResourceKind.Playlist, "37i9dQZweekly"), revision: "cover-revision"), trackCount: 30),
                 new(new("spotify", ProviderResourceKind.Playlist, "37i9dQZdaily"), "Daily Mix", new("spotify"), "revision",
                    artwork: new(new("spotify", ProviderResourceKind.Playlist, "37i9dQZdaily"), revision: "daily-cover"), trackCount: 30)])));
        await service.ListCardsAsync(owner);
        var original = await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await another.Task.WaitAsync(TimeSpan.FromSeconds(5));
        capability.Setup(item => item.GetUserPlaylistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderUserPlaylistsRequest>()))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Success(new("spotify",
                [new(new("spotify", ProviderResourceKind.Playlist, "37i9dQZweekly"), "Discover Weekly", new("spotify"), "revision2",
                    artwork: new(new("spotify", ProviderResourceKind.Playlist, "37i9dQZweekly"), revision: "cover-revision2"), trackCount: 30)])));
        clock.UtcNow = clock.UtcNow.AddHours(7);
        await service.ListCardsAsync(owner);
        var changed = await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(MixCoverImageService.Version(original), MixCoverImageService.Version(changed));
        await service.ListCardsAsync(owner);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task SavedCoverDoesNotResolvePlaybackOrWaitForAFeedRefresh()
    {
        var (capability, _) = await FeedAccount();
        var card = Assert.Single(await service.ListCardsAsync(owner), item => item.Kind == "external");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        capability.Setup(item => item.GetUserPlaylistsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderUserPlaylistsRequest>()))
            .Returns(async () =>
            {
                entered.SetResult();
                await release.Task;
                return ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Success(new("spotify", []));
            });
        clock.UtcNow = clock.UtcNow.AddHours(7);
        var refresh = service.ListCardsAsync(owner);
        await entered.Task;
        gateway.Invocations.Clear();
        try
        {
            var saved = await service.GetCoverAsync(owner, card.Id).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(MixCoverImageService.GetTag(MixCoverInput.From(card)), MixCoverImageService.GetTag(MixCoverInput.From(saved)));
            gateway.VerifyNoOtherCalls();
            capability.Verify(item => item.GetPlaylistTracksAsync(It.IsAny<ProviderExecutionContext>(),
                It.IsAny<ProviderPlaylistTracksRequest>()), Times.Never);
        }
        finally
        {
            release.SetResult();
            await refresh;
        }
    }

    [Fact]
    public async Task Mix_cards_use_the_server_cover_for_local_and_service_songs_and_keep_empty_gradients()
    {
        await Seed();
        await service.GenerateAsync(owner);
        await using (var db = factory.CreateDbContext())
        {
            var row = await db.ExternalMetadataSnapshots.AsNoTracking().Where(item => item.OwnerUserId == owner && item.ResourceKind == "personal-mixes")
                .OrderByDescending(item => item.SnapshotVersion).FirstAsync();
            var document = JsonSerializer.Deserialize<PersonalMixDocument>(row.PayloadJson)!;
            var workout = document.Mixes["workout"];
            document.Mixes["workout"] = workout with
            {
                Songs = Enumerable.Range(0, 4).Select(index => new PersonalMixItem(workout.Id, 1,
                    new Song { Id = $"native-{index}", AlbumId = $"album-{index}", IsLocal = true }, "library", "", 1)).ToArray()
            };
            document.Mixes["discover-weekly"] = document.Mixes["discover-weekly"] with { Songs = document.Mixes["on-repeat"].Songs };
            document.Mixes["release-radar"] = document.Mixes["release-radar"] with { Songs = [] };
            row.Id = Guid.NewGuid();
            row.SnapshotVersion++;
            row.ProviderRevision = row.SnapshotVersion.ToString();
            row.PayloadJson = JsonSerializer.Serialize(document);
            db.ExternalMetadataSnapshots.Add(row);
            await db.SaveChangesAsync();
        }
        var mixes = await service.ListCardsAsync(owner);
        var result = Assert.IsType<OkObjectResult>(await Controller(owner, true).List());
        var cards = JsonSerializer.SerializeToElement(result.Value).EnumerateArray().ToArray();
        foreach (var kind in new[] { "workout", "discover-weekly" })
        {
            var mix = mixes.Single(item => item.Kind == kind);
            Assert.NotEmpty(mix.Songs);
            var card = cards.Single(item => item.GetProperty("Kind").GetString() == kind);
            Assert.Equal($"/api/admin/mixes/{mix.Id}/cover?v={MixCoverImageService.Version(MixCoverInput.From(mix))}",
                Assert.Single(card.GetProperty("coverArtwork").EnumerateArray()).GetString());
        }
        Assert.Empty(cards.Single(item => item.GetProperty("Kind").GetString() == "release-radar").GetProperty("coverArtwork").EnumerateArray());
    }
}
