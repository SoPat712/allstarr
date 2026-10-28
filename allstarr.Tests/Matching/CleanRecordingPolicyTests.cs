using allstarr.Core.Music;
using allstarr.Models.Domain;

namespace allstarr.Tests;

public sealed class CleanRecordingPolicyTests
{
    [Theory]
    [InlineData("Track (Clean)", "Album (Clean)", "Artist", 180, 3, true)]
    [InlineData("Track", "Album", "Artist", 180, 0, true)]
    [InlineData("Track (Clean)", "Album", "Artist", 180, null, false)]
    [InlineData("Track (Live)", "Album", "Artist", 180, 0, false)]
    [InlineData("Track (Remix)", "Album", "Artist", 180, 0, false)]
    [InlineData("Track", "Album", "Other Artist", 180, 0, false)]
    [InlineData("Track", "Other Album", "Artist", 180, 0, false)]
    [InlineData("Track", "Album", "Artist", 210, 0, false)]
    public void CleanCounterpartsRequireRecordingContextAndPositiveRating(string title, string album, string artist,
        int duration, int? rating, bool expected)
    {
        var source = new Song { Id = "explicit", Title = "Track (Explicit)", Album = "Album (Explicit)", Artist = "Artist", Duration = 180, ExplicitContentLyrics = 1 };
        var candidate = new Song { Id = "clean", Title = title, Album = album, Artist = artist, Duration = duration, ExplicitContentLyrics = rating };
        Assert.Equal(expected, CleanRecordingPolicy.IsCounterpart(source, candidate));
        Assert.Null(source.CanonicalRecordingId);
        Assert.Null(candidate.CanonicalRecordingId);
    }
}
