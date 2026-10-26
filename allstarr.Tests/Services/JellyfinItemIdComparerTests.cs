using allstarr.Services.Jellyfin;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace allstarr.Tests;

public sealed class JellyfinItemIdComparerTests
{
    public static TheoryData<string, string, bool> ItemIds => new()
    {
        { "ext-apple-download-song-AbC", "ext-applemusic-song-AbC", true },
        { "ext-apple-download-song-AbC", "ext-applemusic-song-abc", false },
        { "ext-deezer-song-00112233-4455-6677-8899-AABBCCDDEEFF", "ext-deezer-song-00112233-4455-6677-8899-aabbccddeeff", false },
        { "00112233-4455-6677-8899-aabbccddeeff", "00112233445566778899AABBCCDDEEFF", true },
        { "Native-AbC", "Native-abc", false },
        { "ext-deezer-song-AbC", "Native-AbC", false },
        { "EXT-deezer-song-AbC", "ext-deezer-song-AbC", false }
    };

    [Theory]
    [MemberData(nameof(ItemIds))]
    public void ItemIdsUseProviderKindAndOpaqueValueSemantics(string left, string right, bool equal)
    {
        var comparer = JellyfinItemIdComparer.Instance;
        Assert.Equal(equal, comparer.Equals(left, right));
        if (equal) Assert.Equal(comparer.GetHashCode(left), comparer.GetHashCode(right));
    }

    [Fact]
    public void MetadataKeysKeepExpectedCaseAndGuidEquivalenceOnly()
    {
        Assert.Equal(
            JellyfinItemIdComparer.MetadataKey("Audio", "ISRC", "usrc12345678"),
            JellyfinItemIdComparer.MetadataKey("song", "isrc", "USRC12345678"));
        Assert.Equal(
            JellyfinItemIdComparer.MetadataKey("Audio", "MusicBrainzTrack", "00112233-4455-6677-8899-AABBCCDDEEFF"),
            JellyfinItemIdComparer.MetadataKey("song", "musicbrainztrack", "00112233445566778899aabbccddeeff"));
        Assert.NotEqual(
            JellyfinItemIdComparer.MetadataKey("Audio", "deezer", "AbC"),
            JellyfinItemIdComparer.MetadataKey("Audio", "deezer", "abc"));
        Assert.NotEqual(
            JellyfinItemIdComparer.MetadataKey("Audio", "MusicBrainzTrack", "AbC"),
            JellyfinItemIdComparer.MetadataKey("Audio", "MusicBrainzTrack", "abc"));
    }

    [Fact]
    public void UnequalIdsUseOrdinalTieOrdering()
    {
        Assert.True(JellyfinItemIdComparer.Instance.Compare("ext-deezer-song-AbC", "ext-deezer-song-abc") < 0);
        Assert.Equal(0, JellyfinItemIdComparer.Instance.Compare(
            "00112233-4455-6677-8899-aabbccddeeff", "00112233445566778899AABBCCDDEEFF"));
    }

    [Theory]
    [InlineData("notmusicbrainz")]
    [InlineData("musicbrainz-radio")]
    public void UnknownMetadataNamespacesKeepGuidLookingValuesOpaque(string provider)
    {
        Assert.NotEqual(
            JellyfinItemIdComparer.MetadataKey("Audio", provider, "00112233-4455-6677-8899-AABBCCDDEEFF"),
            JellyfinItemIdComparer.MetadataKey("Audio", provider, "00112233-4455-6677-8899-aabbccddeeff"));
    }

    [Theory]
    [InlineData("00112233-4455-6677-8899-aabbccddeeff", "00112233445566778899aabbccddeeff", "00112233-foo")]
    [InlineData("ext-apple-download-song-AbC", "ext-applemusic-song-AbC", "ext-apple-music-song-Aaa")]
    public void EqualIdsKeepTheSameOrderingAgainstOtherIds(string first, string equivalent, string other)
    {
        var comparer = JellyfinItemIdComparer.Instance;
        Assert.True(comparer.Equals(first, equivalent));
        Assert.Equal(Math.Sign(comparer.Compare(first, other)), Math.Sign(comparer.Compare(equivalent, other)));
    }

    [Fact]
    public void ArtistExclusionKeepsTheOtherOpaqueCase()
    {
        var first = Row("ext-deezer-song-one");
        first["ArtistItems"] = new[] { new { Id = "ext-deezer-artist-AbC" } };
        var second = Row("ext-deezer-song-two");
        second["ArtistItems"] = new[] { new { Id = "ext-deezer-artist-abc" } };
        var query = Query("ExcludeArtistIds", "ext-deezer-artist-AbC");

        var rows = query.Apply([first, second], out var total);

        Assert.Equal(1, total);
        Assert.Same(second, Assert.Single(rows));
    }

    [Theory]
    [InlineData("ExcludeItemIds")]
    [InlineData("ExcludeArtistIds")]
    public void NativeGuidExclusionsRecognizeBothWireFormats(string filter)
    {
        var first = Row("00112233-4455-6677-8899-aabbccddeeff");
        first["ArtistItems"] = new[] { new { Id = "00112233-4455-6677-8899-aabbccddeeff" } };
        var second = Row("Native-other");
        var query = Query(filter, "00112233445566778899AABBCCDDEEFF");

        var rows = query.Apply([first, second], out var total);

        Assert.Equal(1, total);
        Assert.Same(second, Assert.Single(rows));
    }

    [Fact]
    public void NativeGuidDeduplicationRetainsTheFirstRow()
    {
        var first = Row("00112233-4455-6677-8899-aabbccddeeff");
        var second = Row("00112233445566778899AABBCCDDEEFF");

        var rows = new JellyfinProviderQuery(QueryCollection.Empty).Apply([first, second], out var total);

        Assert.Equal(1, total);
        Assert.Same(first, Assert.Single(rows));
    }

    private static JellyfinProviderQuery Query(string filter, string value) =>
        new(new QueryCollection(new Dictionary<string, StringValues> { [filter] = value }));

    private static Dictionary<string, object?> Row(string id) => new() { ["Id"] = id, ["Name"] = "Same title", ["Type"] = "Audio" };
}
