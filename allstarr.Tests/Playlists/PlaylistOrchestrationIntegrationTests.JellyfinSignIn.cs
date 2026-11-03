using System.Net;
using allstarr.Core.Playlists;
using allstarr.Core.Secrets;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class PlaylistOrchestrationIntegrationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task JellyfinPlaylistSyncPersistsTheOwnersSavedSignInResult(bool granted, bool rejected)
    {
        _source.Snapshot = Snapshot("owned-before", Entry(0, "one", "source-1", "One"));
        await _service.RunAsync(Context(), new(_link, 9010));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var link = await db.PlaylistLinks.SingleAsync();
            link.MirrorStaleEntries = true;
            link.SyncArtwork = false;
            await db.SaveChangesAsync();
        }
        var options = new SecretStoreOptions { KeyRingPath = Path.Combine(_database.StorageOptions.DataDirectory, "playlist-keys.json") };
        var ring = new FileSecretKeyRingProvider(options);
        await ring.CreateIfMissingAsync(false);
        var secrets = new EncryptedSecretStore(_factory, ring, options, new Clock(_now));
        if (granted) await secrets.StoreJellyfinPlaylistGrantAsync(Context().Principal!, "owner-fixture-token");
        var backend = new BackendPlaylistTargetTests.JellyfinFakeBackend("target-created", "Provider Mix", ["manual", "local-1"])
        { RejectSavedToken = rejected ? HttpStatusCode.Unauthorized : null };
        _source.Snapshot = Snapshot("owned-after", Entry(0, "two", "source-2", "Two"));
        var result = await JellyfinService(backend, secrets).RunAsync(Context(), new(_link, 9011));
        Assert.NotEqual(PlaylistSyncState.Failed, result.State);
        var projection = (await new DurablePlaylistProjectionReader(_factory, new TestBackendLibraryAccess(_factory, "music"))
            .ReadByLinkIdAsync(_user, _link))!;
        if (granted && !rejected)
        {
            Assert.Equal(["local-2", "manual"], backend.Playlists["target-created"].Members);
            Assert.Null(projection.SyncFailureCode);
        }
        else
        {
            Assert.Contains("local-1", backend.Playlists["target-created"].Members);
            Assert.Contains("local-2", backend.Playlists["target-created"].Members);
            Assert.Contains(rejected ? "credential_expired" : "removal", projection.SyncFailureCode);
        }
        if (rejected)
        {
            Assert.Equal("credential_expired", result.ErrorCode);
            Assert.True((await secrets.GetJellyfinPlaylistGrantAsync(Context().Principal!))!.NeedsSignIn);
        }
    }
}
