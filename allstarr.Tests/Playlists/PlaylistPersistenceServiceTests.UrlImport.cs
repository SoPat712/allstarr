using allstarr.Core.Capabilities;
using allstarr.Core.Extensions;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Playlists;
using allstarr.Core.Playlists.Sources;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class PlaylistPersistenceServiceTests
{
    [Fact]
    public async Task ListenBrainzSubscriptionsAndPersonalTokensStayWithTheirExactListener()
    {
        var personal = Guid.CreateVersion7();
        var shared = Guid.CreateVersion7();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.ProviderAccounts.AddRange(
                new ProviderAccountRecord
                {
                    Id = personal,
                    ProviderId = "listenbrainz",
                    OwnerUserId = _userA,
                    DisplayName = "Personal",
                    Enabled = true,
                    CreatedAt = _now,
                    UpdatedAt = _now
                },
                new ProviderAccountRecord
                {
                    Id = shared,
                    ProviderId = "listenbrainz",
                    DisplayName = "Shared",
                    Enabled = true,
                    CreatedAt = _now,
                    UpdatedAt = _now
                });
            await db.SaveChangesAsync();
        }
        var input = Link() with
        {
            ProviderAccountId = personal,
            SourceProviderId = "listenbrainz",
            SourcePlaylistId = "createdfor:weekly-jams",
            SourcePlaylistIdHash = Hash("createdfor:weekly-jams")
        };
        var owner = Context(_userA, "principal-a");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _playlists.CreateLinkAsync(owner, input with { ProviderAccountId = null }));
        await Assert.ThrowsAsync<ArgumentException>(() => _playlists.CreateLinkAsync(owner, input with { ImportMode = PlaylistImportMode.OneTime }));
        var link = await _playlists.CreateLinkAsync(owner, input);
        Assert.Equal(_userA, link.OwnerUserId);
        Assert.Equal(personal, link.ProviderAccountId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _playlists.CreateLinkAsync(Context(_userB, "principal-b"), input));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _playlists.CreateLinkAsync(owner, input with { ProviderAccountId = shared }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadOnlyExtensionImportsThroughTheSourceGatewayAndPersistsAnOwnerScopedPreview(bool sharedLink)
    {
        const string script = """
            registerExtension({getPlaylist:function(id){
              return {id:id,name:'Collection',tracks:[{id:'song',name:'Song',artists:'Artist'}]};
            },handleUrl:function(url){return {type:'playlist',name:'Collection',tracks:[{id:'song',name:'Song',artists:'Artist'}]};}});
            """;
        var normalized = SpotiFlacExtensionCompatibility.NormalizeManifest(
            """{"name":"link-fixture","version":"1.0.0","type":["metadata_provider"]} """, script);
        var manifest = ExtensionSdkV1.ParseManifest(normalized);
        var clients = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var sandbox = new ExtensionSandbox(Path.GetTempPath(), normalized, script, clients.Object,
            NullLogger.Instance, ExtensionRuntimePermissionSet.None);
        var capability = new ExtensionPlaylistCapabilityAdapter(sandbox, manifest);
        var registry = new ProviderRegistry([new(new(manifest.Id, "Link fixture", "Fixture", ProviderOrigin.Extension,
            "1", "1", [new(ProviderCapabilityKind.Playlist, ProviderCapabilitySupportState.Supported,
                ProviderAccountRequirement.None, "1", ["getPlaylistTracks"], supportsPublicRead: true,
                supportsPlaylistDiscovery: false)], new(), entryPoint: "index.js"), [capability])]);
        var clock = new PersistenceClock(DateTimeOffset.UtcNow);
        var accounts = new ProviderAccountResolver(_factory);
        var matches = new TrackMatchCommandService(_factory, new TrackMatchDecisionEngine(), accounts, clock,
            _access, registry: registry);
        var playlists = new PlaylistPersistenceService(_factory, accounts, clock, matches, _access, registry: registry);
        var context = Context(_userA, "principal-a");
        var link = await playlists.CreateLinkAsync(context, Link() with
        {
            ProviderAccountId = null,
            SourceProviderId = manifest.Id,
            SourcePlaylistId = sharedLink ? "https://music.example.test/shared/token" : "collection",
            ImportMode = PlaylistImportMode.OneTime
        });
        var collected = await new ProviderPlaylistSourceGateway(registry, new ProviderPlaylistSnapshotCollector(), _factory, clock)
            .CollectAsync(context, link, default);
        Assert.Equal("Collection", collected.Name);
        var track = Assert.Single(collected.Entries);
        Assert.Equal("song", track.TrackId!.Value);
        var metadata = await matches.CaptureSnapshotAsync(context, Snapshot(1, track.TrackId.Value) with
        {
            ProviderAccountId = null,
            ProviderId = manifest.Id
        });
        var persisted = await playlists.CaptureSourceSnapshotAsync(context, link.Id,
            new(1, collected.SourceRevision, null, collected.Name, null, null, Hash("collection"),
                [new(0, metadata.Id, track.SourceEntryIdHash)]));
        var preview = await playlists.ReadPreviewAsync(context, link.Id, persisted.Id);
        Assert.Single(preview.Entries);
        Assert.Null(link.ProviderAccountId);
        Assert.False(registry.GetRequired(manifest.Id).Capabilities.Single().SupportsPlaylistDiscovery);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => playlists.GetLinkAsync(Context(_userB, "principal-b"), link.Id));
        clients.VerifyNoOtherCalls();
    }
}
