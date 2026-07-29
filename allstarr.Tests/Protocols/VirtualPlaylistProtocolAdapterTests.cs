using System.Text.Json;
using System.Xml.Linq;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Protocols.Jellyfin;
using allstarr.Core.Protocols.Subsonic;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Tests;

public sealed class VirtualPlaylistProtocolAdapterTests
{
    private static readonly Guid LinkId = Guid.Parse("0198a537-719c-7ea8-9e5a-17e1f2f963f0");
    private static readonly string ProtocolId = PlaylistVirtualizationService.CreateProtocolId(LinkId);

    [Fact]
    public async Task JellyfinRead_PreservesSourceOrderAndUsesOnlyLocalBackendIds()
    {
        var adapter = new JellyfinVirtualPlaylistProtocolAdapter(new StubVirtualizationService(Model()));
        var result = Assert.IsType<JsonResult>(await adapter.ReadItemsAsync(
            Context(ProtocolKind.Jellyfin), ProtocolId, CancellationToken.None));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        var items = json.RootElement.GetProperty("Items");

        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal("jellyfin-local-b", items[0].GetProperty("Id").GetString());
        Assert.Equal(3, items[0].GetProperty("IndexNumber").GetInt32());
        Assert.Equal("jellyfin-local-a", items[1].GetProperty("Id").GetString());
        Assert.All(items.EnumerateArray(), item => Assert.Equal(ProtocolId, item.GetProperty("ParentId").GetString()));
    }

    [Fact]
    public async Task JellyfinList_PublishesCompleteDiscoverablePlaylistSummaries()
    {
        var adapter = new JellyfinVirtualPlaylistProtocolAdapter(new StubVirtualizationService(Model()));

        var item = Assert.Single(await adapter.ListItemsAsync(
            Context(ProtocolKind.Jellyfin), CancellationToken.None));

        Assert.Equal(ProtocolId, item["Id"]);
        Assert.Equal("Road Trip", item["Name"]);
        Assert.Equal("Playlist", item["Type"]);
        Assert.Equal("Audio", item["MediaType"]);
        Assert.Equal(2, item["ChildCount"]);
        Assert.True((long)item["RunTimeTicks"]! > 0);
        Assert.NotEmpty(Assert.IsType<Dictionary<string, string>>(item["ProviderIds"]));
    }

    [Fact]
    public async Task SubsonicRead_ShapesJsonAndXmlWithoutExternalStreamIds()
    {
        var adapter = new SubsonicVirtualPlaylistProtocolAdapter(
            new StubVirtualizationService(Model()),
            new StubMutationResolver(null));
        var jsonResult = Assert.IsType<JsonResult>(await adapter.ReadAsync(
            Context(ProtocolKind.Subsonic), ProtocolId, "json", CancellationToken.None));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(jsonResult.Value));
        var entries = json.RootElement.GetProperty("subsonic-response").GetProperty("playlist").GetProperty("entry");
        Assert.Equal("jellyfin-local-b", entries[0].GetProperty("id").GetString());
        Assert.DoesNotContain("ext-", JsonSerializer.Serialize(jsonResult.Value), StringComparison.Ordinal);

        var xmlResult = Assert.IsType<ContentResult>(await adapter.ReadAsync(
            Context(ProtocolKind.Subsonic), ProtocolId, "xml", CancellationToken.None));
        var document = XDocument.Parse(xmlResult.Content!);
        var ns = document.Root!.Name.Namespace;
        Assert.Equal(new[] { "jellyfin-local-b", "jellyfin-local-a" },
            document.Descendants(ns + "entry").Select(item => item.Attribute("id")!.Value));
    }

    [Fact]
    public async Task Adapters_ReturnNullForUnlinkedOrUnknownVirtualPlaylist()
    {
        var service = new StubVirtualizationService(null);
        Assert.Null(await new JellyfinVirtualPlaylistProtocolAdapter(service).ReadItemAsync(
            Context(ProtocolKind.Jellyfin), ProtocolId, CancellationToken.None));
        Assert.Null(await new SubsonicVirtualPlaylistProtocolAdapter(service, new StubMutationResolver(null)).ReadAsync(
            Context(ProtocolKind.Subsonic), ProtocolId, "json", CancellationToken.None));
        Assert.False(PlaylistVirtualizationService.TryParseProtocolId("ext-spotify-playlist-123", out _));
    }

    private static VirtualPlaylistReadModel Model() => new(
        ProtocolId, LinkId, Guid.CreateVersion7(), "Road Trip", "Source description", "artwork-key",
        "apple-music", "revision-7", PlaylistLinkMode.Hybrid,
        [
            new(2, "jellyfin-local-b", "Second", "Artist B", "Album B", null, 2000, "cover-b", TrackMatchState.Accepted),
            new(8, "jellyfin-local-a", "Ninth", "Artist A", "Album A", "Artist A", 3000, null, TrackMatchState.Pinned)
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
    }

    private sealed class StubMutationResolver(SubsonicPlaylistMutationRoute? route)
        : ISubsonicPlaylistMutationResolver
    {
        public Task<SubsonicPlaylistMutationRoute?> ResolveAsync(
            ProtocolExecutionContext context,
            string protocolId,
            CancellationToken cancellationToken = default) => Task.FromResult(route);
    }
}
