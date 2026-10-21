using System.Reflection;
using System.Text.Json;
using allstarr.Controllers;
using allstarr.Services.Subsonic;

namespace allstarr.Tests;

public sealed class SubsonicPlaylistParameterTests
{
    [Fact]
    public void PlaylistValues_JsonBodyRetainsTypesAndReplacesOnlySongArray()
    {
        var parameters = new SubsonicRequestParameters("POST", "application/json",
            """{"playlistId":"target","songId":["native","ext-deezer-song-A"],"public":true,"future":{"keep":7}}""",
            [new("playlistId", "target", SubsonicParameterSource.Json),
                new("songId", "native", SubsonicParameterSource.Json),
                new("songId", "ext-deezer-song-A", SubsonicParameterSource.Json),
                new("public", "true", SubsonicParameterSource.Json)]);
        var rewritten = Rewrite(parameters, "songId", ["native"]);
        Assert.Equal("application/json", rewritten.ContentType);
        using var json = JsonDocument.Parse(rewritten.RawBody!);
        Assert.Equal(["native"], json.RootElement.GetProperty("songId").EnumerateArray().Select(item => item.GetString()));
        Assert.True(json.RootElement.GetProperty("public").GetBoolean());
        Assert.Equal(7, json.RootElement.GetProperty("future").GetProperty("keep").GetInt32());
    }

    [Fact]
    public void PlaylistValues_SplitQueryAndFormRemovesProviderIdsFromEverySource()
    {
        var parameters = new SubsonicRequestParameters("POST", "application/x-www-form-urlencoded",
            "u=owner&songIdToAdd=ext-deezer-song-A&future=keep",
            [new("songIdToAdd", "native", SubsonicParameterSource.Query),
                new("u", "owner", SubsonicParameterSource.Form),
                new("songIdToAdd", "ext-deezer-song-A", SubsonicParameterSource.Form),
                new("future", "keep", SubsonicParameterSource.Form)]);
        var rewritten = Rewrite(parameters, "songIdToAdd", ["native"]);
        Assert.Equal(["native"], rewritten.GetAllValues("songIdToAdd"));
        Assert.DoesNotContain("ext-", rewritten.RawBody);
        Assert.Contains("future=keep", rewritten.RawBody);
        Assert.Contains("u=owner", rewritten.RawBody);
    }

    private static SubsonicRequestParameters Rewrite(SubsonicRequestParameters parameters, string name, string[] values) =>
        (SubsonicRequestParameters)typeof(SubsonicController).GetMethod("PlaylistValues", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [parameters, name, values])!;
}
