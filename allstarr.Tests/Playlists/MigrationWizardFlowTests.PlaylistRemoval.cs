using allstarr.Core.Playlists.Targets;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class MigrationWizardFlowTests
{
    [Fact]
    public async Task PlaylistReaddThroughAccountImportKeepsMatchesAndSchedule()
    {
        var first = await Selected("owned", keepInSync: true);
        await Finish(first);
        await Service().ChangeAsync(Context(owner), first, "apply", null, default);
        await Finish(first);
        await using var before = factory.CreateDbContext();
        var link = await before.PlaylistLinks.SingleAsync();
        var matchIds = await before.TrackMatches.OrderBy(item => item.Id).Select(item => item.Id).ToArrayAsync();
        var scheduleId = link.ScheduleId;
        var savedMembers = new BackendPlaylistMember[] { new("manual", "manual"), new("local-one", "local-one") };
        destination = destination! with { Members = savedMembers };
        target.Setup(item => item.ReadItemsAsync(It.IsAny<BackendPlaylistTargetContext>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendPlaylistTargetResult<IReadOnlyList<BackendPlaylistMember>>(BackendPlaylistTargetStatus.Success, savedMembers));
        target.Setup(item => item.DeleteAsync(It.IsAny<BackendPlaylistTargetContext>(), "destination", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                destination = null;
                return new(BackendPlaylistTargetStatus.Success, true);
            });
        await orchestration.RemoveFromAppsAsync(Context(owner), link.Id, link.Revision);
        var second = await Service().StartAsync(Context(owner), account, "add-again", default);
        await Service().DiscoverAsync(Context(owner), second, null, default);
        await Service().ChangeAsync(Context(owner), second, "choose", [new("owned", true)], default);
        await Finish(second);
        await Service().ChangeAsync(Context(owner), second, "apply", null, default);
        await Finish(second);
        await using var after = factory.CreateDbContext();
        var restored = await after.PlaylistLinks.SingleAsync();
        Assert.Equal(link.Id, restored.Id);
        Assert.True(restored.Enabled);
        Assert.Equal(scheduleId, restored.ScheduleId);
        Assert.True((await after.JobSchedules.SingleAsync()).Enabled);
        Assert.Equal(matchIds, await after.TrackMatches.OrderBy(item => item.Id).Select(item => item.Id).ToArrayAsync());
        Assert.Equal("destination", restored.TargetPlaylistId);
        Assert.Equal(["manual", "local-one"], destination!.Members.Select(item => item.BackendItemId));
        Assert.Single(await after.AuditEvents.Where(item => item.Action == "added-to-apps").ToArrayAsync());
    }
}
