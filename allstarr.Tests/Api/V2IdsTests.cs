using allstarr.Core.ClientApiV2;

namespace allstarr.Tests.Api;

public sealed class V2IdsTests
{
    [Fact]
    public void RecordingTracksRoundTrip()
    {
        var recording = Guid.NewGuid();
        Assert.True(V2Ids.TryDecode(V2Ids.Track(recording), V2IdKind.Track, out var id));
        Assert.Equal(recording, id.Recording);
        Assert.Null(id.Source);
    }

    [Theory]
    [InlineData("ext-deezer-song-12345")]
    [InlineData("0f8d2a1c9b7e4d6f8a2b3c4d5e6f7a8b")]
    [InlineData("ext-apple-song-i.1440833098")]
    public void SourceTracksRoundTripAndOlderSpellingsAreAccepted(string source)
    {
        Assert.True(V2Ids.TryDecode(V2Ids.TrackSource(source), V2IdKind.Track, out var wrapped));
        Assert.Equal(source, wrapped.Source);
        Assert.True(V2Ids.TryDecode(source, V2IdKind.Track, out var plain));
        Assert.Equal(source, plain.Source);
    }

    [Theory]
    [InlineData("alb_ZXh0LWRlZXplci1hbGJ1bS0x")]
    [InlineData("ext-deezer-album-1")]
    [InlineData("ext-deezer-song-1,ext-deezer-song-2")]
    [InlineData("ext-deezer-song-../../etc")]
    [InlineData("ext-deezer-song-1?x=1")]
    [InlineData("trk_s!!")]
    [InlineData("trk_")]
    [InlineData("native id")]
    [InlineData("")]
    public void TracksRejectOtherKindsAndUnsafeIds(string value) =>
        Assert.False(V2Ids.TryDecode(value, V2IdKind.Track, out _));

    [Fact]
    public void WrappedIdsMustDecodeToTheirOwnKind()
    {
        Assert.False(V2Ids.TryDecode(V2Ids.TrackSource("ext-deezer-album-1"), V2IdKind.Track, out _));
        Assert.True(V2Ids.TryDecode(V2Ids.Album("ext-deezer-album-1"), V2IdKind.Album, out var album));
        Assert.Equal("ext-deezer-album-1", album.Source);
    }

    [Fact]
    public void ArtworkKeysResolveToTheImageRouteWithAClampedWidth()
    {
        var artwork = V2ArtworkUrls.For("native1", "songtag");
        Assert.NotNull(artwork);
        var key = artwork.Url[V2ArtworkUrls.PathPrefix.Length..];
        var resolved = V2ArtworkUrls.Resolve(key, "9000");
        Assert.NotNull(resolved);
        Assert.Equal("/Items/native1/Images/Primary", resolved.Value.Path);
        Assert.Equal("?tag=songtag&maxWidth=2048&quality=90", resolved.Value.Query.Value);
        Assert.Equal("?tag=songtag&maxWidth=600&quality=90", V2ArtworkUrls.Resolve(key, null)!.Value.Query.Value);
    }

    [Theory]
    [InlineData("native1", "tag with space")]
    [InlineData("native/1", "tag")]
    [InlineData("native1", "")]
    public void ArtworkNeedsASafeItemAndTag(string item, string tag) => Assert.Null(V2ArtworkUrls.For(item, tag));

    [Theory]
    [InlineData("not base64!")]
    [InlineData("bmF0aXZlMQ")]
    [InlineData("")]
    public void ArtworkKeysRejectAnythingTheyDidNotIssue(string key) => Assert.Null(V2ArtworkUrls.Resolve(key, "300"));
}
