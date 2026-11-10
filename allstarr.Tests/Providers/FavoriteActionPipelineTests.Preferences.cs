using System.Text.Json;
using allstarr.Core.Favorites;
using allstarr.Core.Settings;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class FavoriteActionPipelineTests
{
    [Theory]
    [InlineData("heart", null)]
    [InlineData("keep", "download")]
    [InlineData("addToLibrary", "add-to-library")]
    public async Task FavoritePreference_SnapshotsOnlyTheListenersChosenAction(string value, string? expected)
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.RuntimeSettings.Add(new()
            {
                Id = Guid.CreateVersion7(), OwnerUserId = _userId, Key = ListeningPreferenceKeys.HeartAction,
                ValueType = RuntimeSettingValueType.String, ValueJson = JsonSerializer.Serialize(value),
                Source = "personal-preferences", Revision = 1
            });
            await db.SaveChangesAsync();
        }
        var request = Request(FavoriteOperation.Favorite, "preference") with { ItemId = "ext-fixture-song-track-1" };
        var receipt = await _pipeline.RecordAsync(request);
        await using var verify = await _factory.CreateDbContextAsync();
        var actions = await verify.FavoriteActions.Where(item => item.EventId == receipt.EventId).Select(item => item.ActionType).ToArrayAsync();
        Assert.Contains(FavoriteActionPipeline.VirtualLikedAction, actions);
        Assert.Equal(expected == null ? 1 : 2, actions.Length);
        if (expected != null) Assert.Contains(expected, actions);
        var other = await _pipeline.RecordAsync(Request(FavoriteOperation.Favorite, "preference", _otherUserId) with { ItemId = request.ItemId });
        Assert.Contains("download", await verify.FavoriteActions.Where(item => item.EventId == other.EventId).Select(item => item.ActionType).ToArrayAsync());
        var unfavorite = await _pipeline.RecordAsync(request with { Operation = FavoriteOperation.Unfavorite, SourceRevision = "off" });
        Assert.Equal(FavoriteActionPipeline.VirtualLikedAction,
            await verify.FavoriteActions.Where(item => item.EventId == unfavorite.EventId).Select(item => item.ActionType).SingleAsync());
    }

    [Fact]
    public async Task FavoritePreference_LibraryActionExecutesAfterTheHeart()
    {
        var receipt = await _pipeline.RecordAsync(Request(FavoriteOperation.Favorite, "library-action"));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.FavoriteActions.Add(new()
            {
                Id = Guid.CreateVersion7(), EventId = receipt.EventId, OwnerUserId = _userId,
                ActionType = "add-to-library", IdempotencyKey = "library-action"
            });
            await db.SaveChangesAsync();
        }
        var actions = new List<string>();
        var handler = new FavoriteActionJobHandler(_factory, [new RecordingExecutor("add-to-library", actions)], _clock);
        var claim = await _jobs.ClaimNextAsync("heart-library", [FavoriteActionPipeline.JobType]);
        Assert.Equal(allstarr.Core.Jobs.DurableJobCompletionKind.Succeeded,
            (await handler.ExecuteAsync(new(claim!, EmptyServices.Instance), default)).Kind);
        Assert.Equal(["add-to-library"], actions);
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.True((await verify.FavoriteStates.SingleAsync()).IsFavorite);
    }
}
