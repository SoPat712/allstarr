using System.Text.Json;
using allstarr.Core.Matching;

namespace allstarr.Tests;

public sealed class TrackSnapshotMetadataTests
{
    [Theory]
    [InlineData("""{"artist":"Nicky Youre","artists":["Nicky Youre","hey daisy"]}""")]
    [InlineData("""{"Artist":"Nicky Youre","Artists":["Nicky Youre","hey daisy"]}""")]
    [InlineData("""{"artists":[{"name":"Nicky Youre"},{"name":"hey daisy"},null,42]}""")]
    public void KeepsFullArtistCreditsAcrossSnapshotFormats(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal("Nicky Youre, hey daisy", TrackSnapshotMetadata.Artist(document.RootElement));
    }

    [Theory]
    [InlineData("""{"artists":[],"artist":"Nicky Youre"}""")]
    [InlineData("""{"Artists":[null,42,""],"primaryArtist":"Nicky Youre"}""")]
    public void UsesPrimaryArtistWhenNoUsableCreditsExist(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal("Nicky Youre", TrackSnapshotMetadata.Artist(document.RootElement));
    }
}
