using allstarr.Core.Capabilities;

namespace allstarr.Tests.Providers;

public sealed partial class ArtistGridProviderTests
{
    [Fact]
    public async Task TrackerFileDateNotesAndEraArtworkReachTrackAndAlbum()
    {
        const string payload = """
            {"name":"Artist","tab":{"gid":"g-1"},"eras":[{"name":"Era","tracks":[
              {"name":"Song","file_date":"Jul 17, 2020","leak_date":"Jun 19, 2023","notes":"Early demo.",
               "type":"Demo","quality":"Lossless","og_filename":"song.wav","sub_era":"Sessions",
               "links":[{"url":"https://pillows.su/f/song"}]}]}]}
            """;
        using var client = Client(request => request.RequestUri!.AbsolutePath == "/artists.csv"
            ? Response(Csv("Artist", "artist"), null, "text/csv") : Response(payload, null, "application/json"));
        var adapter = Metadata(client);
        var song = Assert.Single((await adapter.SearchTracksAsync(Context(), new("Artist Song", new(10)))).RequireValue().Items);
        Assert.Equal("2020-07-17", song.ReleaseDate);
        Assert.Equal(2020, song.Year);
        Assert.Equal("Era", song.AlbumTitle);
        Assert.NotNull(song.AlbumId);
        Assert.NotNull(song.Artwork);
        Assert.Contains("Early demo.", song.Notes);
        Assert.Contains("Demo", song.Notes);
        Assert.Contains("song.wav", song.Notes);
        Assert.Contains("2023-06-19", song.Notes);
        Assert.DoesNotContain("https://", song.Notes);
        var album = (await adapter.GetAlbumAsync(Context(), new(song.AlbumId!))).RequireValue();
        Assert.Equal("Era", album.Title);
        Assert.Single(album.Tracks);
        Assert.NotNull(album.Artwork);
    }
}
