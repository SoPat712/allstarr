using allstarr.Core.Playlists.Targets;

namespace allstarr.Tests;

public sealed partial class BackendPlaylistTargetTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JellyfinOneTimeImportPreservesUserOrderAndStaleSongs(bool actsAsUser)
    {
        var backend = new JellyfinFakeBackend("p1", "Mix", ["b", "a", "stale"]);
        var target = new JellyfinPlaylistTarget(new HttpClient(backend), new Uri("https://jellyfin.test/"),
            new FakeAuthenticationResolver(actsAsUser: actsAsUser));
        var result = await target.WriteAsync(Context(), new BackendPlaylistWriteRequest(
            BackendPlaylistWriteMode.Reconcile, new("Mix"), ["a", "b", "new"], "import-order", "p1",
            syncOwnedBackendItemIds: ["a", "b", "stale"], removeStaleSyncOwnedItems: true)
        { PreserveExistingOrder = true }, default);
        Assert.True(result.IsSuccess);
        Assert.Equal(["b", "a", "stale", "new"], backend.Playlists["p1"].Members);
        Assert.Empty(result.Value!.UnsupportedMetadataFields);
        Assert.DoesNotContain(backend.Requests, item => item.Path.Contains("/Move/", StringComparison.Ordinal));
    }
}
