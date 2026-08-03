using System.Net;
using allstarr.Core.Protocols;
using allstarr.Core.Protocols.Subsonic;
using allstarr.Core.Playback;
using allstarr.Models.Domain;
using allstarr.Models.Search;
using allstarr.Services.Subsonic;
using Microsoft.AspNetCore.Http;

namespace allstarr.Tests;

public sealed class SubsonicProtocolAdapterTests
{
    [Fact]
    public void SearchWindow_UsesIndependentOffsetsAndCounts()
    {
        var parameters = new SubsonicRequestParameters(
            "GET",
            null,
            null,
            [
                new("query", " fixture ", SubsonicParameterSource.Query),
                new("songCount", "2", SubsonicParameterSource.Query),
                new("songOffset", "1", SubsonicParameterSource.Query),
                new("albumCount", "1", SubsonicParameterSource.Query),
                new("albumOffset", "2", SubsonicParameterSource.Query),
                new("artistCount", "1", SubsonicParameterSource.Query),
                new("artistOffset", "1", SubsonicParameterSource.Query)
            ]);
        var context = new ProtocolExecutionContext(
            ProtocolKind.Subsonic,
            "backend",
            "verified-user",
            null,
            "correlation",
            DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None);
        var adapter = new SubsonicSearchProtocolAdapter();

        var window = adapter.Parse(parameters, context);
        var result = adapter.ApplyWindow(new SearchResult
        {
            Songs = Enumerable.Range(1, 4).Select(i => new Song { Id = $"song-{i}" }).ToList(),
            Albums = Enumerable.Range(1, 4).Select(i => new Album { Id = $"album-{i}" }).ToList(),
            Artists = Enumerable.Range(1, 4).Select(i => new Artist { Id = $"artist-{i}" }).ToList()
        }, window);

        Assert.Equal(3, window.SongFetchCount);
        Assert.Equal(3, window.AlbumFetchCount);
        Assert.Equal(2, window.ArtistFetchCount);
        Assert.Equal(["song-2", "song-3"], result.Songs.Select(song => song.Id));
        Assert.Equal(["album-3"], result.Albums.Select(album => album.Id));
        Assert.Equal(["artist-2"], result.Artists.Select(artist => artist.Id));
    }

    [Fact]
    public void SearchWindow_RejectsAnotherProtocolContext()
    {
        var context = new ProtocolExecutionContext(
            ProtocolKind.Jellyfin,
            "backend",
            "verified-user",
            null,
            "correlation",
            DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None);

        Assert.Throws<InvalidOperationException>(() =>
            new SubsonicSearchProtocolAdapter().Parse(
                SubsonicRequestParameters.FromDictionary(new Dictionary<string, string>()),
                context));
    }

    [Fact]
    public void ScrobbleSignals_PreserveRepeatedIdsTimesAndSubmissionMeaning()
    {
        var receivedAt = DateTimeOffset.FromUnixTimeMilliseconds(5_000);
        var parameters = new SubsonicRequestParameters(
            "POST",
            "application/x-www-form-urlencoded",
            null,
            [
                new("id", "song-a", SubsonicParameterSource.Form),
                new("id", "song-b", SubsonicParameterSource.Form),
                new("time", "1000", SubsonicParameterSource.Form),
                new("time", "2000", SubsonicParameterSource.Form),
                new("submission", "false", SubsonicParameterSource.Form),
                new("submission", "true", SubsonicParameterSource.Form)
            ]);

        var signals = new SubsonicScrobbleProtocolAdapter().Parse(parameters, receivedAt);

        Assert.Collection(
            signals,
            first =>
            {
                Assert.Equal("song-a", first.ItemId);
                Assert.Equal(PlaybackTransition.Start, first.Transition);
                Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1000), first.ObservedAt);
                Assert.Equal("1000", first.EventKey);
            },
            second =>
            {
                Assert.Equal("song-b", second.ItemId);
                Assert.Equal(PlaybackTransition.Submission, second.Transition);
                Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(2000), second.ObservedAt);
                Assert.Equal("2000", second.EventKey);
            });
    }

    [Fact]
    public void ScrobbleSignals_DefaultToSubmissionAndUseRetryDedupeBucket()
    {
        var receivedAt = DateTimeOffset.FromUnixTimeSeconds(61);
        var parameters = SubsonicRequestParameters.FromDictionary(
            new Dictionary<string, string> { ["id"] = "song-a" });

        var signal = Assert.Single(new SubsonicScrobbleProtocolAdapter().Parse(parameters, receivedAt));

        Assert.Equal(PlaybackTransition.Submission, signal.Transition);
        Assert.Equal(receivedAt, signal.ObservedAt);
        Assert.Equal("2", signal.EventKey);
    }

    [Fact]
    public void RequestParameters_SetValuePreservesCredentialSourcesAndAddsNewQueryValue()
    {
        var parameters = new SubsonicRequestParameters(
            "POST",
            "application/x-www-form-urlencoded",
            "u=fixture&p=secret",
            [
                new("u", "fixture", SubsonicParameterSource.Form),
                new("p", "secret", SubsonicParameterSource.Form),
                new("f", "xml", SubsonicParameterSource.Query)
            ]);

        var updated = parameters.SetValue("f", "json").SetValue("id", "song-1");

        Assert.Equal(SubsonicParameterSource.Form, updated.Ordered.Single(item => item.Name == "u").Source);
        Assert.Equal(SubsonicParameterSource.Form, updated.Ordered.Single(item => item.Name == "p").Source);
        Assert.Equal(SubsonicParameterSource.Query, updated.Ordered.Single(item => item.Name == "id").Source);
        Assert.Equal("u=fixture&p=secret", updated.RawBody);
        Assert.Equal("json", updated["f"]);
    }

    [Fact]
    public void ControllerRecordsEachRepeatedFavoriteTrackInsteadOfACommaJoinedId()
    {
        var controller = File.ReadAllText(FindRepositoryFile(
            "allstarr", "Controllers", "SubSonicController.cs"));

        Assert.Contains("parameters.GetAllValues(\"id\")", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("var itemId = parameters.GetValueOrDefault(\"id\", \"\");\n                if (!string.IsNullOrWhiteSpace(itemId))", controller, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "allstarr.sln")))
            directory = directory.Parent;
        return Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException(), Path.Combine(parts));
    }
}
