using allstarr.Core.Music;
using allstarr.Models.Domain;

namespace allstarr.Tests;

public sealed class SearchRecordingProofTests
{
    private static Song Walk(string album = "Walk", int duration = 152, string? isrc = null, string title = "Walk", int? explicitContent = null,
        Guid? recording = null) => new()
        {
            Title = title,
            Artist = "Griff",
            Album = album,
            Duration = duration,
            Isrc = isrc,
            ExplicitContentLyrics = explicitContent,
            CanonicalRecordingId = recording
        };

    [Fact]
    public void SearchJoinsCopiesOnlyOnProof()
    {
        var recording = Guid.NewGuid();
        // The same saved recording is proof on its own.
        Assert.True(MusicSearchService.SameSearchRecording(Walk("Walk", recording: recording), Walk("vertigo", 160, recording: recording)));
        // A shared ISRC proves a single and its album copy are one recording.
        Assert.True(MusicSearchService.SameSearchRecording(Walk("Walk", isrc: "GBAHT2100001"), Walk("vertigo", 153, "GB-AHT-21-00001")));
        // So does the same album, with or without "- Single".
        Assert.True(MusicSearchService.SameSearchRecording(Walk("Walk - Single"), Walk("Walk", 153)));
    }

    [Fact]
    public void SearchKeepsCopiesWithoutProofApart()
    {
        // Title, artist and length alone are not enough: another album without a shared ISRC stays its own row.
        Assert.False(MusicSearchService.SameSearchRecording(Walk("Walk"), Walk("vertigo")));
        Assert.False(MusicSearchService.SameSearchRecording(Walk("Walk"), Walk("")));
        Assert.False(MusicSearchService.SameSearchRecording(Walk("Walk", isrc: "GBAHT2100001"), Walk("vertigo", isrc: "GBAHT2100002")));
        // Even a shared ISRC never joins a different length, version or explicitness.
        Assert.False(MusicSearchService.SameSearchRecording(Walk(isrc: "GBAHT2100001"), Walk(duration: 155, isrc: "GBAHT2100001")));
        Assert.False(MusicSearchService.SameSearchRecording(Walk(isrc: "GBAHT2100001"), Walk(title: "Walk (Live)", isrc: "GBAHT2100001")));
        Assert.False(MusicSearchService.SameSearchRecording(Walk(title: "Walk (In The Room)"), Walk()));
        Assert.False(MusicSearchService.SameSearchRecording(Walk(isrc: "GBAHT2100001", explicitContent: 1),
            Walk(isrc: "GBAHT2100001", explicitContent: 0)));
        // A copy without a length can't be checked, so it stays apart.
        Assert.False(MusicSearchService.SameSearchRecording(Walk(duration: 0, isrc: "GBAHT2100001"), Walk(isrc: "GBAHT2100001")));
    }

    [Fact]
    public void SearchJoinsAServicesCopyOfTheSameReleaseOnly()
    {
        Album Single(string title, int? year = 2025, int? tracks = 1) =>
            new() { Title = title, Artist = "AT Musica", Year = year, SongCount = tracks };
        const string title = "I just wanna make a lot of money, be my own housewife";
        Assert.True(MusicSearchService.SameSearchRelease(Single(title + " - Single"), Single(title)));
        Assert.True(MusicSearchService.SameSearchRelease(Single(title, year: null), Single(title, tracks: null)));
        Assert.False(MusicSearchService.SameSearchRelease(Single(title), Single(title, year: 2024)));
        Assert.False(MusicSearchService.SameSearchRelease(Single(title), Single(title, tracks: 3)));
        Assert.False(MusicSearchService.SameSearchRelease(Single(title), Single(title + " (Deluxe)")));
        Assert.False(MusicSearchService.SameSearchRelease(Single(title), new Album { Title = title, Artist = "Someone else" }));
    }
}
