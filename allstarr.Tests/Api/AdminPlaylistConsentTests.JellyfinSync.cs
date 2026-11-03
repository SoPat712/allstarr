using System.Net;
using allstarr.Core.Playlists.Targets;
using allstarr.Models.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace allstarr.Tests;

public sealed partial class AdminPlaylistConsentTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task JellyfinPlaylistGrantReconcilesOrFallsBackWhenRejected(HttpStatusCode? rejection)
    {
        var alice = (await _identities.ResolveAsync(new("jellyfin", "alice-id", "Alice")))!;
        await _secrets.StoreJellyfinPlaylistGrantAsync(alice, "alice-fixture-token");
        var backend = new BackendPlaylistTargetTests.JellyfinFakeBackend("p1", "Mix", ["b", "a", "stale"])
        { RejectSavedToken = rejection };
        var target = new JellyfinPlaylistTarget(new Clients(backend), Options.Create(new JellyfinSettings
        { Url = "http://backend.test", ApiKey = "server-key" }), _secrets);
        var result = await target.WriteAsync(new(alice.BackendInstanceId, "alice-id"), new(
            BackendPlaylistWriteMode.Reconcile, new("Mix"), ["a", "b", "new"], "saved-sync", "p1",
            syncOwnedBackendItemIds: ["a", "b", "stale"], removeStaleSyncOwnedItems: true), default);
        Assert.True(result.IsSuccess);
        if (rejection == null)
        {
            Assert.Equal(["a", "b", "new"], backend.Playlists["p1"].Members);
            Assert.Empty(result.Value!.UnsupportedMetadataFields);
            Assert.Contains(backend.Requests, item => item.Path.Contains("/Move/", StringComparison.Ordinal));
        }
        else
        {
            Assert.Contains("stale", backend.Playlists["p1"].Members);
            Assert.Contains("new", backend.Playlists["p1"].Members);
            Assert.Equal(["credential_expired"], result.Value!.UnsupportedMetadataFields);
            Assert.True((await _secrets.GetJellyfinPlaylistGrantAsync(alice))!.NeedsSignIn);
            var session = await _sessions.CreateSessionAsync("alice-id", "Alice", false, "session-token", null,
                backendType: "Jellyfin", allstarrUserId: alice.UserId);
            var status = Assert.IsType<OkObjectResult>(await Controller(session.SessionId, "Jellyfin").GetPlaylistConsent(default));
            Assert.True(System.Text.Json.JsonSerializer.SerializeToElement(status.Value).GetProperty("needsSignIn").GetBoolean());
            Assert.DoesNotContain(backend.Requests, item => item.Path.Contains("/Move/", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task JellyfinPlaylistTokenRejectionDuringCreationReusesTheNewCopy()
    {
        var alice = (await _identities.ResolveAsync(new("jellyfin", "alice-id", "Alice")))!;
        await _secrets.StoreJellyfinPlaylistGrantAsync(alice, "alice-fixture-token");
        var backend = new BackendPlaylistTargetTests.JellyfinFakeBackend("p1", "Existing", [])
        { RejectTokenAfterCreate = true };
        var target = new JellyfinPlaylistTarget(new Clients(backend), Options.Create(new JellyfinSettings
        { Url = "http://backend.test", ApiKey = "server-key" }), _secrets);
        var result = await target.WriteAsync(new(alice.BackendInstanceId, "alice-id"), new(
            BackendPlaylistWriteMode.Reconcile, new("Mix"), ["a", "b"], "new-copy"), default);
        Assert.True(result.IsSuccess);
        Assert.Equal("p2", result.Value!.Snapshot.BackendPlaylistId);
        Assert.Equal(2, backend.Playlists.Count);
        Assert.Equal(["a", "b"], backend.Playlists["p2"].Members);
        Assert.Equal(["credential_expired"], result.Value.UnsupportedMetadataFields);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JellyfinPlaylistTokensNeverCrossListenerOwners(bool bobGranted)
    {
        var alice = (await _identities.ResolveAsync(new("jellyfin", "alice-id", "Alice")))!;
        var bob = (await _identities.ResolveAsync(new("jellyfin", "bob-id", "Bob")))!;
        await _secrets.StoreJellyfinPlaylistGrantAsync(alice, "alice-fixture-token");
        if (bobGranted) await _secrets.StoreJellyfinPlaylistGrantAsync(bob, "bob-fixture-token");
        var backend = new BackendPlaylistTargetTests.JellyfinFakeBackend("p1", "Mix", ["b", "a", "stale"]);
        var target = new JellyfinPlaylistTarget(new Clients(backend), Options.Create(new JellyfinSettings
        { Url = "http://backend.test", ApiKey = "server-key" }), _secrets);
        var result = await target.WriteAsync(new(bob.BackendInstanceId, "bob-id"), new(
            BackendPlaylistWriteMode.Reconcile, new("Mix"), ["a", "b"], "bob-sync", "p1",
            syncOwnedBackendItemIds: ["stale"], removeStaleSyncOwnedItems: true), default);
        Assert.True(result.IsSuccess);
        if (bobGranted)
        {
            Assert.Empty(result.Value!.UnsupportedMetadataFields);
            Assert.All(backend.Requests, item => Assert.Contains("bob-fixture-token", item.Authorization));
        }
        else
        {
            Assert.Contains("order", result.Value!.UnsupportedMetadataFields);
            Assert.Contains("removal", result.Value.UnsupportedMetadataFields);
        }
        Assert.All(backend.Requests, item => Assert.DoesNotContain("alice-fixture-token", item.Authorization));
        Assert.Equal(bobGranted, await _secrets.GetJellyfinPlaylistGrantAsync(bob) != null);
    }
}
