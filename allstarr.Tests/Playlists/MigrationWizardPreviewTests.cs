using System.Text.Json;
using allstarr.Core.Migrations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class MigrationWizardFlowTests
{
    [Fact]
    public async Task AccountPreviewListsRealCollectionsWithoutCreatingImportWork()
    {
        var preview = JsonSerializer.SerializeToElement(await Service().PreviewAsync(Context(owner), account, null, default));

        Assert.Equal(3, preview.GetProperty("items").GetArrayLength());
        Assert.Contains(preview.GetProperty("items").EnumerateArray(), item => item.GetProperty("sourceId").GetString() == "liked-songs");
        Assert.Equal(2, preview.GetProperty("unsupported").GetArrayLength());
        await using var db = factory.CreateDbContext();
        Assert.Empty(await db.Set<CollectionMigrationRunRecord>().ToArrayAsync());
        Assert.Empty(await db.Set<CollectionMigrationCollectionRecord>().ToArrayAsync());
        Assert.Empty(await db.Jobs.ToArrayAsync());
        Assert.Empty(await db.PlaylistLinks.ToArrayAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service().PreviewAsync(Context(other), account, null, default));
    }

    [Fact]
    public async Task PlaylistImportCanStayInAllstarrWithoutCreatingABackendPlaylist()
    {
        var run = await Start();
        await Service().DiscoverAsync(Context(owner), run, null, default);
        await Service().ChangeAsync(Context(owner), run, "choose", [new("owned")], default, makePlaylists: false);
        await Finish(run);
        await Service().ChangeAsync(Context(owner), run, "apply", null, default);
        await Finish(run);

        Assert.False((await Service().StatusAsync(Context(owner), run, default)).Run.MakePlaylists);
        await using var db = factory.CreateDbContext();
        var link = Assert.Single(await db.PlaylistLinks.ToArrayAsync());
        Assert.Equal(PlaylistLinkMode.Virtual, link.Mode);
        Assert.Null(link.TargetPlaylistId);
        Assert.Equal(0, creates);
        Assert.Equal(0, finds);
    }
}
