using System.Text.Json;
using allstarr.Core.Playlists.Targets;
using static allstarr.Tests.AudioMuseTests;

namespace allstarr.Tests;

public sealed class AudioMusePlaylistTests
{
    [Fact]
    public async Task Private_creation_sets_native_privacy_and_preserves_listener_and_order()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Playlists")
            {
                using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                Assert.False(body.RootElement.GetProperty("isPublic").GetBoolean());
                Assert.Contains("UserId=listener", request.RequestUri.Query, StringComparison.Ordinal);
                Assert.Contains("Ids=two%2Cone", request.RequestUri.Query, StringComparison.OrdinalIgnoreCase);
                return Fixture.Json("{\"Id\":\"ordinary\"}");
            }
            if (request.RequestUri.AbsolutePath == "/Users/listener/Items/ordinary")
                return Fixture.Json("{\"Id\":\"ordinary\",\"Name\":\"Saved\"}");
            if (request.RequestUri.AbsolutePath == "/Playlists/ordinary/Items")
                return Fixture.Json("{\"Items\":[{\"Id\":\"two\"},{\"Id\":\"one\"}]}");
            return Fixture.Json("{}");
        }));
        var target = new JellyfinPlaylistTarget(http, new Uri("http://backend.example"));
        var result = await target.WriteAsync(new("primary", "listener"), new(BackendPlaylistWriteMode.Recreate,
            new("Saved") { Private = true }, ["two", "one"], "preview"), default);
        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { "two", "one" }, result.Value!.Snapshot.Members.Select(m => m.BackendItemId));
    }
}
