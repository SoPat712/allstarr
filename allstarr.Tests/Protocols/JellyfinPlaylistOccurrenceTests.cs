using System.Text.Json;
using allstarr.Core.Matching;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Protocols.Jellyfin;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using Moq;

namespace allstarr.Tests;

public sealed class JellyfinPlaylistOccurrenceTests
{
    [Fact]
    public async Task PlaylistOccurrence_DurableEntriesKeepIdentityAcrossPositionAndRouteChanges()
    {
        var firstId = Guid.Parse("0198a537-719c-7ea8-9e5a-17e1f2f963f1");
        var secondId = Guid.Parse("0198a537-719c-7ea8-9e5a-17e1f2f963f2");
        var first = Resolved(firstId, 0, "42");
        var second = Resolved(secondId, 1, "42");
        var initial = await Entries([first, second]);
        Assert.NotEqual(initial[0], initial[1]);
        var moved = await Entries([Resolved(secondId, 0, "77"), Resolved(firstId, 1, "42")]);
        Assert.Equal(initial[1], moved[0]);
        Assert.Equal(initial[0], moved[1]);
        Assert.Equal(initial[1], Assert.Single(await Entries([first, second], 1, 1)));

        var source = WithSourceEntry(new DurablePlaylistSourceEntryProjection(7, Guid.NewGuid(),
            new("spotify", null, "hash", "revision", 1, "source"), new("Source", ["Artist"])), firstId);
        var sourceTrack = PlaylistVirtualizationService.ToSourceVirtualTrack(source, first);
        Assert.Equal(initial[0], Assert.Single(await Entries([sourceTrack], mode: PlaylistProjectionMode.Source)));
    }

    [Fact]
    public async Task PlaylistOccurrence_NativeItemsKeepTheirFieldsAndNativeEntryId()
    {
        const string nativeJson = """{"Id":"native","Name":"Native","AlbumId":"album","ParentId":"native-album","AlbumPrimaryImageTag":"art","UnknownFutureField":{"Keep":true},"ProviderIds":{"MusicBrainzTrack":"recording"}}""";
        var track = new VirtualPlaylistTrack(0, "native", "Native", "Artist", "Album", null,
            1000, null, TrackMatchState.Accepted, "spotify", NativePlaylistEntryId: "native-entry", NativeEntryJson: nativeJson);
        var duplicate = track with
        {
            SourcePosition = 1,
            NativePlaylistEntryId = "native-entry-2",
            NativeEntryJson = nativeJson.Replace("true", "false", StringComparison.Ordinal)
        };
        var items = await Items([track, duplicate], PlaylistProjectionMode.Target);
        Assert.Equal(2, items.Length);
        for (var index = 0; index < items.Length; index++)
        {
            using var expected = JsonDocument.Parse(index == 0 ? track.NativeEntryJson! : duplicate.NativeEntryJson!);
            foreach (var property in expected.RootElement.EnumerateObject())
                Assert.Equal(property.Value.GetRawText(), items[index].GetProperty(property.Name).GetRawText());
        }
        Assert.Equal("native-entry", items[0].GetProperty("PlaylistItemId").GetString());
        Assert.Equal("native-entry-2", items[1].GetProperty("PlaylistItemId").GetString());
    }

    private static VirtualPlaylistTrack Resolved(Guid id, int position, string externalId)
    {
        var entry = WithSourceEntry(new DurablePlaylistEntryProjection(position, Guid.NewGuid(), externalId,
            "Repeated", ["Artist"], "Album", null, 1000, null, null, TrackMatchState.Accepted,
            null, "external", "deezer", []), id);
        return PlaylistVirtualizationService.ToResolvedVirtualTrack(entry,
            new Dictionary<string, LibraryTrackRecord>(), "spotify");
    }

    private static T WithSourceEntry<T>(T value, Guid id)
    {
        typeof(T).GetProperty("SourceEntryId")?.SetValue(value, id);
        return value;
    }

    private static async Task<string[]> Entries(IReadOnlyList<VirtualPlaylistTrack> tracks,
        int start = 0, int limit = int.MaxValue, PlaylistProjectionMode mode = PlaylistProjectionMode.Resolved) =>
        (await Items(tracks, mode, start, limit)).Select(item => item.GetProperty("PlaylistItemId").GetString()!).ToArray();

    private static async Task<JsonElement[]> Items(IReadOnlyList<VirtualPlaylistTrack> tracks,
        PlaylistProjectionMode mode, int start = 0, int limit = int.MaxValue)
    {
        var linkId = Guid.Parse("0198a537-719c-7ea8-9e5a-17e1f2f963f0");
        var id = PlaylistVirtualizationService.CreateProtocolId(linkId);
        var model = new VirtualPlaylistReadModel(id, linkId, Guid.NewGuid(), "Playlist", null,
            null, "spotify", "source", "revision", PlaylistLinkMode.Virtual, tracks, mode);
        var service = new Mock<IPlaylistVirtualizationService>();
        service.Setup(item => item.ReadAsync(It.IsAny<ProtocolExecutionContext>(), id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(model);
        var adapter = new JellyfinVirtualPlaylistProtocolAdapter(service.Object,
            Mock.Of<IJellyfinPlaylistMutationResolver>());
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "backend", "principal",
            null, "correlation", DateTimeOffset.UtcNow.AddMinutes(1), default);
        var query = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["StartIndex"] = start.ToString(),
            ["Limit"] = limit.ToString()
        });
        var result = Assert.IsType<JsonResult>(await adapter.ReadItemsAsync(context, id, null, query, default));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        return json.RootElement.GetProperty("Items").EnumerateArray().Select(item => item.Clone()).ToArray();
    }
}
