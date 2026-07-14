using System.Net;
using allstarr.Core.Protocols;
using allstarr.Core.Protocols.Subsonic;
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
}
