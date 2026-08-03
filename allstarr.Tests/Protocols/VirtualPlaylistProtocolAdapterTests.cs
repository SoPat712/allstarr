using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using allstarr.Controllers;
using allstarr.Core.Matching;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Protocols.Jellyfin;
using allstarr.Core.Protocols.Subsonic;
using allstarr.Core.Storage;
using allstarr.Services.Subsonic;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Tests;

public sealed class VirtualPlaylistProtocolAdapterTests
{
    private static readonly Guid LinkId = Guid.Parse("0198a537-719c-7ea8-9e5a-17e1f2f963f0");
    private static readonly string ProtocolId = PlaylistVirtualizationService.CreateProtocolId(LinkId);

    [Fact]
    public async Task JellyfinRead_PreservesEverySourceRowWithoutInventingPlayback()
    {
        var adapter = new JellyfinVirtualPlaylistProtocolAdapter(
            new StubVirtualizationService(Model()),
            new StubJellyfinMutationResolver(null));
        var result = Assert.IsType<JsonResult>(await adapter.ReadItemsAsync(
            Context(ProtocolKind.Jellyfin), ProtocolId, CancellationToken.None));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        var items = json.RootElement.GetProperty("Items");

        Assert.Equal(3, items.GetArrayLength());
        Assert.Equal("jellyfin-local-b", items[0].GetProperty("Id").GetString());
        Assert.Equal(3, items[0].GetProperty("IndexNumber").GetInt32());
        Assert.Equal("jellyfin-local-a", items[1].GetProperty("Id").GetString());
        var unresolved = items[2];
        Assert.Equal("allstarr-unresolved-source-hash", unresolved.GetProperty("Id").GetString());
        Assert.Equal("None", unresolved.GetProperty("PlayAccess").GetString());
        Assert.False(unresolved.GetProperty("CanDownload").GetBoolean());
        Assert.Empty(unresolved.GetProperty("MediaSources").EnumerateArray());
        Assert.Equal("apple-music",
            unresolved.GetProperty("ProviderIds").GetProperty("AllstarrSource").GetString());
        Assert.All(items.EnumerateArray(), item => Assert.False(item.TryGetProperty("ParentId", out _)));
        Assert.All(items.EnumerateArray(), item =>
        {
            var id = item.GetProperty("Id").GetString();
            var userData = item.GetProperty("UserData");
            Assert.Equal(id, userData.GetProperty("ItemId").GetString());
            Assert.Equal(id, userData.GetProperty("Key").GetString());
        });
        Assert.All(items.EnumerateArray(), item => Assert.False(item.TryGetProperty("ArtistItems", out _)));
    }

    [Fact]
    public async Task JellyfinList_PublishesCompleteDiscoverablePlaylistSummaries()
    {
        var adapter = new JellyfinVirtualPlaylistProtocolAdapter(
            new StubVirtualizationService(Model()),
            new StubJellyfinMutationResolver(null));

        var item = Assert.Single(await adapter.ListItemsAsync(
            Context(ProtocolKind.Jellyfin), CancellationToken.None));

        Assert.Equal(ProtocolId, item["Id"]);
        Assert.Equal("Road Trip", item["Name"]);
        Assert.Equal("Playlist", item["Type"]);
        Assert.Equal("Audio", item["MediaType"]);
        Assert.Equal(3, item["ChildCount"]);
        Assert.True((long)item["RunTimeTicks"]! > 0);
        var userData = Assert.IsType<Dictionary<string, object>>(item["UserData"]);
        Assert.Equal(ProtocolId, userData["ItemId"]);
        Assert.Equal(ProtocolId, userData["Key"]);
        Assert.NotEmpty(Assert.IsType<Dictionary<string, string>>(item["ProviderIds"]));
        var definitionResult = Assert.IsType<JsonResult>(await adapter.ReadDefinitionAsync(
            Context(ProtocolKind.Jellyfin), ProtocolId, CancellationToken.None));
        using var definition = JsonDocument.Parse(JsonSerializer.Serialize(definitionResult.Value));
        Assert.False(definition.RootElement.GetProperty("OpenAccess").GetBoolean());
        Assert.Empty(definition.RootElement.GetProperty("Shares").EnumerateArray());
        Assert.Equal(
            ["jellyfin-local-b", "jellyfin-local-a", "allstarr-unresolved-source-hash"],
            definition.RootElement.GetProperty("ItemIds").EnumerateArray()
                .Select(value => value.GetString()!).ToArray());
        Assert.Equal(
            "ext-apple-music-playlist-source-playlist",
            await adapter.GetImageSourceIdAsync(
                Context(ProtocolKind.Jellyfin), ProtocolId, CancellationToken.None));
        Assert.Equal(
            "ext-apple-music-playlist-source-playlist",
            await adapter.GetImageSourceIdAsync(null, ProtocolId, CancellationToken.None));
    }

    [Fact]
    public async Task JellyfinList_PublishesWritableHybridProjection()
    {
        var adapter = new JellyfinVirtualPlaylistProtocolAdapter(
            new StubVirtualizationService(Model()),
            new StubJellyfinMutationResolver(
                new JellyfinPlaylistMutationRoute(true, "backend-playlist")));

        Assert.Equal(
            ProtocolId,
            Assert.Single(await adapter.ListItemsAsync(
                Context(ProtocolKind.Jellyfin), CancellationToken.None))["Id"]);
    }

    [Fact]
    public async Task JellyfinSourceRead_KeepsSourceMetadataWithoutNativeMediaFacts()
    {
        var source = Model() with
        {
            ProjectionMode = PlaylistProjectionMode.Source,
            Tracks =
            [
                new(0, "jellyfin-local-a", "Source title", "Source artist", "Source album",
                    null, 1_000, "ext-spotify-song-source-a", TrackMatchState.Accepted,
                    "spotify", "source-a", TrackRouteKind.Local,
                    SourceIdentity: new("spotify", Guid.NewGuid(), "source-hash", "revision-7", 7, "source-a"),
                    SourceMetadata: new("Source title", ["Source artist", "Second artist"],
                        "Source album", 1_000, "spotify", "USRC17607839"))
            ]
        };
        var adapter = new JellyfinVirtualPlaylistProtocolAdapter(
            new StubVirtualizationService(source),
            new StubJellyfinMutationResolver(null));

        var result = Assert.IsType<JsonResult>(await adapter.ReadItemsAsync(
            Context(ProtocolKind.Jellyfin), ProtocolId, CancellationToken.None));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        var item = json.RootElement.GetProperty("Items")[0];

        Assert.Contains("Source title", item.GetProperty("Name").GetString(), StringComparison.Ordinal);
        Assert.Contains("Source artist", item.GetProperty("Artists")[0].GetString(), StringComparison.Ordinal);
        Assert.Contains("Second artist", item.GetProperty("Artists")[1].GetString(), StringComparison.Ordinal);
        Assert.Equal("Virtual", item.GetProperty("LocationType").GetString());
        Assert.Empty(item.GetProperty("MediaSources").EnumerateArray());
        Assert.Equal("spotify", item.GetProperty("ProviderIds").GetProperty("AllstarrSource").GetString());
        Assert.Equal("source-a", item.GetProperty("ProviderIds").GetProperty("spotify").GetString());
        Assert.Equal("source-hash", item.GetProperty("ProviderIds").GetProperty("AllstarrSourceHash").GetString());
        Assert.Equal("revision-7", item.GetProperty("ProviderIds").GetProperty("AllstarrSourceRevision").GetString());
        Assert.Equal("USRC17607839", item.GetProperty("ProviderIds").GetProperty("ISRC").GetString());
    }

    [Fact]
    public async Task SubsonicRead_PreservesJsonAndXmlOrderIncludingUnresolvedRows()
    {
        var adapter = new SubsonicVirtualPlaylistProtocolAdapter(
            new StubVirtualizationService(Model()),
            new StubMutationResolver(null));
        var jsonResult = Assert.IsType<JsonResult>(await adapter.ReadAsync(
            Context(ProtocolKind.Subsonic), ProtocolId, "json", CancellationToken.None));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(jsonResult.Value));
        var subsonicPlaylist = json.RootElement.GetProperty("subsonic-response").GetProperty("playlist");
        var entries = subsonicPlaylist.GetProperty("entry");
        Assert.Equal(3, subsonicPlaylist.GetProperty("songCount").GetInt32());
        Assert.Equal(3, entries.GetArrayLength());
        Assert.Equal("jellyfin-local-b", entries[0].GetProperty("id").GetString());
        Assert.Equal("allstarr-unresolved-source-hash", entries[2].GetProperty("id").GetString());
        Assert.Equal("apple-music", entries[2].GetProperty("allstarrSource").GetString());
        Assert.Equal("source-hash", entries[2].GetProperty("allstarrSourceHash").GetString());
        Assert.DoesNotContain("ext-", JsonSerializer.Serialize(jsonResult.Value), StringComparison.Ordinal);

        var xmlResult = Assert.IsType<ContentResult>(await adapter.ReadAsync(
            Context(ProtocolKind.Subsonic), ProtocolId, "xml", CancellationToken.None));
        var document = XDocument.Parse(xmlResult.Content!);
        var ns = document.Root!.Name.Namespace;
        Assert.Equal(new[] { "jellyfin-local-b", "jellyfin-local-a", "allstarr-unresolved-source-hash" },
            document.Descendants(ns + "entry").Select(item => item.Attribute("id")!.Value));
    }

    [Fact]
    public async Task SubsonicRead_OmitsUnknownDurationAndArtworkFacts()
    {
        var model = Model() with
        {
            ArtworkReferenceKey = null,
            Tracks =
            [
                new(0, "unresolved", "Unknown", "Artist", null, null, null, null,
                    TrackMatchState.Unresolved, RouteKind: TrackRouteKind.Unresolved)
            ]
        };
        var adapter = new SubsonicVirtualPlaylistProtocolAdapter(
            new StubVirtualizationService(model),
            new StubMutationResolver(null));

        var result = Assert.IsType<JsonResult>(await adapter.ReadAsync(
            Context(ProtocolKind.Subsonic), ProtocolId, "json", CancellationToken.None));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        var playlist = json.RootElement.GetProperty("subsonic-response").GetProperty("playlist");
        var entry = playlist.GetProperty("entry")[0];

        Assert.False(playlist.TryGetProperty("duration", out _));
        Assert.False(playlist.TryGetProperty("coverArt", out _));
        Assert.False(entry.TryGetProperty("duration", out _));
        Assert.False(entry.TryGetProperty("album", out _));
        Assert.False(entry.TryGetProperty("coverArt", out _));
    }

    [Fact]
    public async Task SubsonicList_AppendsVirtualSummariesAfterNativePlaylists()
    {
        var model = Model() with
        {
            Name = "Virtual Mix",
            Description = "Virtual comment",
            ArtworkReferenceKey = "virtual-cover"
        };
        var adapter = new SubsonicVirtualPlaylistProtocolAdapter(
            new StubVirtualizationService(model),
            new StubMutationResolver(null));
        var native = new SubsonicProxyResponse(
            Encoding.UTF8.GetBytes("""{"subsonic-response":{"status":"ok","version":"1.16.1","playlists":{"playlist":{"id":"native-playlist","name":"Native","owner":"backend","public":true,"songCount":1,"unknownField":"preserved"}}}}"""),
            "application/json; charset=utf-8",
            System.Net.HttpStatusCode.OK,
            new Dictionary<string, string[]> { ["X-Native"] = ["kept"] });

        var merged = await adapter.ListAsync(
            Context(ProtocolKind.Subsonic), "json", native, CancellationToken.None);

        Assert.Equal(native.StatusCode, merged.StatusCode);
        Assert.Equal(native.Headers, merged.Headers);
        using var json = JsonDocument.Parse(merged.Body);
        var response = json.RootElement.GetProperty("subsonic-response");
        var playlists = response.GetProperty("playlists").GetProperty("playlist");
        Assert.Equal(["native-playlist", ProtocolId],
            playlists.EnumerateArray().Select(item => item.GetProperty("id").GetString()));
        Assert.Equal("preserved", playlists[0].GetProperty("unknownField").GetString());
        Assert.Equal("Virtual Mix", playlists[1].GetProperty("name").GetString());
        Assert.Equal("Virtual comment", playlists[1].GetProperty("comment").GetString());
        Assert.Equal("allstarr", playlists[1].GetProperty("owner").GetString());
        Assert.False(playlists[1].GetProperty("public").GetBoolean());
        Assert.Equal(3, playlists[1].GetProperty("songCount").GetInt32());
        Assert.Equal(9, playlists[1].GetProperty("duration").GetInt64());
        Assert.Equal("virtual-cover", playlists[1].GetProperty("coverArt").GetString());

        var passthrough = await new SubsonicVirtualPlaylistProtocolAdapter(
            new StubVirtualizationService(null), new StubMutationResolver(null)).ListAsync(
                Context(ProtocolKind.Subsonic), "json", native, CancellationToken.None);
        Assert.Same(native, passthrough);

        var malformed = native with
        {
            Body = Encoding.UTF8.GetBytes(
                """{"subsonic-response":{"status":"ok","playlists":{"playlist":42}}}""")
        };
        Assert.Same(malformed, await adapter.ListAsync(
            Context(ProtocolKind.Subsonic), "json", malformed, CancellationToken.None));
    }

    [Fact]
    public async Task SubsonicTargetRead_PreservesNativeJsonAndXmlEntries()
    {
        var target = Model() with
        {
            ProjectionMode = PlaylistProjectionMode.Target,
            TargetPlaylistId = "backend-target",
            Tracks =
            [
                new(0, "native-b", "ignored", "ignored", null, null, 2_000, null,
                    TrackMatchState.Unresolved, RouteKind: TrackRouteKind.Local,
                    NativeEntryJson: "{\"id\":\"native-b\",\"title\":\"Native B\",\"artistId\":\"artist-b\",\"albumId\":\"album-b\",\"coverArt\":\"cover-b\",\"provider\":\"navidrome\",\"unknownField\":\"kept-b\"}"),
                new(1, "native-a", "ignored", "ignored", null, null, 1_000, null,
                    TrackMatchState.Unresolved, RouteKind: TrackRouteKind.Local,
                    NativeEntryJson: "{\"id\":\"native-a\",\"title\":\"Native A\",\"artistId\":\"artist-a\",\"albumId\":\"album-a\",\"coverArt\":\"cover-a\",\"provider\":\"navidrome\",\"unknownField\":\"kept-a\"}")
            ]
        };
        var adapter = new SubsonicVirtualPlaylistProtocolAdapter(
            new StubVirtualizationService(target),
            new StubMutationResolver(null));

        var jellyfin = new JellyfinVirtualPlaylistProtocolAdapter(
            new StubVirtualizationService(target),
            new StubJellyfinMutationResolver(null));
        Assert.Equal("backend-target", await jellyfin.GetImageSourceIdAsync(
            Context(ProtocolKind.Jellyfin), ProtocolId, CancellationToken.None));
        Assert.Equal("backend-target", await jellyfin.GetImageSourceIdAsync(
            null, ProtocolId, CancellationToken.None));

        var jsonResult = Assert.IsType<JsonResult>(await adapter.ReadAsync(
            Context(ProtocolKind.Subsonic), ProtocolId, "json", CancellationToken.None));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(jsonResult.Value));
        var entries = json.RootElement.GetProperty("subsonic-response").GetProperty("playlist").GetProperty("entry");
        Assert.Equal(["native-b", "native-a"], entries.EnumerateArray().Select(item => item.GetProperty("id").GetString()));
        Assert.Equal("artist-b", entries[0].GetProperty("artistId").GetString());
        Assert.Equal("album-b", entries[0].GetProperty("albumId").GetString());
        Assert.Equal("cover-b", entries[0].GetProperty("coverArt").GetString());
        Assert.Equal("navidrome", entries[0].GetProperty("provider").GetString());
        Assert.Equal("kept-b", entries[0].GetProperty("unknownField").GetString());

        var xmlResult = Assert.IsType<ContentResult>(await adapter.ReadAsync(
            Context(ProtocolKind.Subsonic), ProtocolId, "xml", CancellationToken.None));
        var document = XDocument.Parse(xmlResult.Content!);
        var ns = document.Root!.Name.Namespace;
        var xmlEntries = document.Descendants(ns + "entry").ToArray();
        Assert.Equal(["native-b", "native-a"], xmlEntries.Select(item => item.Attribute("id")!.Value));
        Assert.Equal("artist-b", xmlEntries[0].Attribute("artistId")!.Value);
        Assert.Equal("album-b", xmlEntries[0].Attribute("albumId")!.Value);
        Assert.Equal("cover-b", xmlEntries[0].Attribute("coverArt")!.Value);
        Assert.Equal("navidrome", xmlEntries[0].Attribute("provider")!.Value);
        Assert.Equal("kept-b", xmlEntries[0].Attribute("unknownField")!.Value);

        var resolvedAdapter = new SubsonicVirtualPlaylistProtocolAdapter(
            new StubVirtualizationService(target with { ProjectionMode = PlaylistProjectionMode.Resolved }),
            new StubMutationResolver(null));
        var resolvedJsonResult = Assert.IsType<JsonResult>(await resolvedAdapter.ReadAsync(
            Context(ProtocolKind.Subsonic), ProtocolId, "json", CancellationToken.None));
        using var resolvedJson = JsonDocument.Parse(JsonSerializer.Serialize(resolvedJsonResult.Value));
        var resolvedEntries = resolvedJson.RootElement.GetProperty("subsonic-response")
            .GetProperty("playlist").GetProperty("entry");
        Assert.Equal(["native-b", "native-a"],
            resolvedEntries.EnumerateArray().Select(item => item.GetProperty("id").GetString()));
        Assert.Equal("kept-b", resolvedEntries[0].GetProperty("unknownField").GetString());

        var resolvedXmlResult = Assert.IsType<ContentResult>(await resolvedAdapter.ReadAsync(
            Context(ProtocolKind.Subsonic), ProtocolId, "xml", CancellationToken.None));
        var resolvedDocument = XDocument.Parse(resolvedXmlResult.Content!);
        var resolvedNs = resolvedDocument.Root!.Name.Namespace;
        Assert.Equal("kept-b", resolvedDocument.Descendants(resolvedNs + "entry")
            .First().Attribute("unknownField")!.Value);
    }

    [Theory]
    [InlineData(PlaylistProjectionMode.Source)]
    [InlineData(PlaylistProjectionMode.Target)]
    public async Task AdminAndProtocols_PreserveProjectionCountAndOrder(PlaylistProjectionMode mode)
    {
        var model = Model() with { ProjectionMode = mode };
        using var admin = JsonDocument.Parse(JsonSerializer.Serialize(
            PlaylistLinksController.ToClientProjectionDto(model)));
        var adminIds = admin.RootElement.GetProperty("tracks").EnumerateArray()
            .Select(item => item.GetProperty("itemId").GetString()).ToArray();

        var service = new StubVirtualizationService(model);
        var subsonicResult = Assert.IsType<JsonResult>(await new SubsonicVirtualPlaylistProtocolAdapter(
            service, new StubMutationResolver(null)).ReadAsync(
                Context(ProtocolKind.Subsonic), ProtocolId, "json", CancellationToken.None));
        using var subsonic = JsonDocument.Parse(JsonSerializer.Serialize(subsonicResult.Value));
        var subsonicIds = subsonic.RootElement.GetProperty("subsonic-response")
            .GetProperty("playlist").GetProperty("entry").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()).ToArray();
        var jellyfinResult = Assert.IsType<JsonResult>(await new JellyfinVirtualPlaylistProtocolAdapter(
            service, new StubJellyfinMutationResolver(null)).ReadItemsAsync(
                Context(ProtocolKind.Jellyfin), ProtocolId, CancellationToken.None));
        using var jellyfin = JsonDocument.Parse(JsonSerializer.Serialize(jellyfinResult.Value));
        var jellyfinIds = jellyfin.RootElement.GetProperty("Items").EnumerateArray()
            .Select(item => item.GetProperty("Id").GetString()).ToArray();

        Assert.Equal(mode.ToString().ToLowerInvariant(),
            admin.RootElement.GetProperty("projectionMode").GetString());
        Assert.Equal(adminIds.Length, admin.RootElement.GetProperty("trackCount").GetInt32());
        Assert.Equal(adminIds, subsonicIds);
        Assert.Equal(adminIds, jellyfinIds);
    }

    [Fact]
    public async Task Adapters_ReturnNullForUnlinkedOrUnknownVirtualPlaylist()
    {
        var service = new StubVirtualizationService(null);
        Assert.Null(await new JellyfinVirtualPlaylistProtocolAdapter(
            service,
            new StubJellyfinMutationResolver(null)).ReadItemAsync(
            Context(ProtocolKind.Jellyfin), ProtocolId, CancellationToken.None));
        Assert.Null(await new SubsonicVirtualPlaylistProtocolAdapter(service, new StubMutationResolver(null)).ReadAsync(
            Context(ProtocolKind.Subsonic), ProtocolId, "json", CancellationToken.None));
        Assert.False(PlaylistVirtualizationService.TryParseProtocolId("ext-spotify-playlist-123", out _));
    }

    private static VirtualPlaylistReadModel Model() => new(
        ProtocolId, LinkId, Guid.CreateVersion7(), "Road Trip", "Source description", "artwork-key",
        "apple-music", "source-playlist", "revision-7", PlaylistLinkMode.Hybrid,
        [
            new(2, "jellyfin-local-b", "Second", "Artist B", "Album B", null, 2000, "cover-b", TrackMatchState.Accepted),
            new(8, "jellyfin-local-a", "Ninth", "Artist A", "Album A", "Artist A", 3000, null, TrackMatchState.Pinned),
            new(12, "allstarr-unresolved-source-hash", "Missing", "Artist C", "Album C", null,
                4000, null, TrackMatchState.Unresolved, "apple-music", null, TrackRouteKind.Unresolved,
                SourceIdentity: new("apple-music", Guid.NewGuid(), "source-hash", "revision-7", 7),
                SourceMetadata: new("Missing", ["Artist C"], "Album C", 4_000))
        ]);

    private static ProtocolExecutionContext Context(ProtocolKind protocol) => new(
        protocol, "backend", "principal", null, "correlation", DateTimeOffset.UtcNow.AddMinutes(1),
        CancellationToken.None);

    private sealed class StubVirtualizationService(VirtualPlaylistReadModel? model) : IPlaylistVirtualizationService
    {
        public Task<IReadOnlyList<VirtualPlaylistReadModel>> ListAsync(
            ProtocolExecutionContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<VirtualPlaylistReadModel>>(model == null ? [] : [model]);

        public Task<VirtualPlaylistReadModel?> ReadAsync(
            ProtocolExecutionContext context, string protocolId, CancellationToken cancellationToken = default) =>
            Task.FromResult(model);

        public Task<VirtualPlaylistReadModel?> ReadAsync(
            ProtocolExecutionContext context,
            string protocolId,
            PlaylistProjectionMode projectionMode,
            CancellationToken cancellationToken = default) => Task.FromResult(
                model == null ? null : model with { ProjectionMode = projectionMode });

        public Task<VirtualPlaylistReadModel?> ReadBySourceAsync(
            ProtocolExecutionContext context,
            string sourceProviderId,
            string sourcePlaylistId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(model);

        public Task<VirtualPlaylistArtworkSource?> ResolvePublicArtworkSourceAsync(
            string protocolId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(model == null
                ? null
                : new VirtualPlaylistArtworkSource(
                    model.SourceProviderId, model.SourcePlaylistId, model.TargetPlaylistId));
    }

    private sealed class StubMutationResolver(SubsonicPlaylistMutationRoute? route)
        : ISubsonicPlaylistMutationResolver
    {
        public Task<SubsonicPlaylistMutationRoute?> ResolveAsync(
            ProtocolExecutionContext context,
            string protocolId,
            CancellationToken cancellationToken = default) => Task.FromResult(route);
    }

    private sealed class StubJellyfinMutationResolver(JellyfinPlaylistMutationRoute? route)
        : IJellyfinPlaylistMutationResolver
    {
        public Task<JellyfinPlaylistMutationRoute?> ResolveAsync(
            ProtocolExecutionContext context,
            string protocolId,
            CancellationToken cancellationToken = default) => Task.FromResult(route);
    }
}
