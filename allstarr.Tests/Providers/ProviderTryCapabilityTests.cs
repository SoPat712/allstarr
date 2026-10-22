using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Moq;

namespace allstarr.Tests;

public sealed class ProviderTryCapabilityTests
{
    [Theory]
    [InlineData("albums", "success")]
    [InlineData("artists", "success")]
    [InlineData("albums", "empty")]
    [InlineData("artists", "empty")]
    [InlineData("albums", "error")]
    [InlineData("artists", "error")]
    public async Task Metadata_search_projects_each_kind_with_twenty_item_limit(string kind, string mode)
    {
        var metadata = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        if (kind == "albums")
            metadata.Setup(item => item.SearchAlbumsAsync(It.IsAny<ProviderExecutionContext>(),
                    It.Is<ProviderMetadataSearchRequest>(request => request.Query == "sample" && request.Page.Limit == 20)))
                .ReturnsAsync(mode == "error" ? ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>.Failure(new(ProviderErrorKind.TransientFailure)) :
                    ProviderOutcome<ProviderPage<ProviderAlbumMetadata>>.Success(new("fixture", mode == "empty" ? [] :
                        Enumerable.Range(0, 25).Select(index => new ProviderAlbumMetadata(
                            new("fixture", ProviderResourceKind.Album, index.ToString()), "Album", [new("Artist")])))));
        else
            metadata.Setup(item => item.SearchArtistsAsync(It.IsAny<ProviderExecutionContext>(),
                    It.Is<ProviderMetadataSearchRequest>(request => request.Query == "sample" && request.Page.Limit == 20)))
                .ReturnsAsync(mode == "error" ? ProviderOutcome<ProviderPage<ProviderArtistMetadata>>.Failure(new(ProviderErrorKind.TransientFailure)) :
                    ProviderOutcome<ProviderPage<ProviderArtistMetadata>>.Success(new("fixture", mode == "empty" ? [] :
                        Enumerable.Range(0, 25).Select(index => new ProviderArtistMetadata(
                            new("fixture", ProviderResourceKind.Artist, index.ToString()), "Artist")))));
        var operations = Operations(metadata.Object);

        var result = await operations.ExecuteAsync(ProviderTryOperationsTests.Context(), new()
        { Capability = "metadata", Operation = "search", Kind = kind, Query = "sample" });

        Assert.Equal(mode == "success", result.Succeeded);
        Assert.Equal(mode == "success" ? null : mode == "empty" ? "not_found" : "transient-failure", result.ReasonCode);
        var json = Json(result);
        AssertEnvelope(json);
        if (mode != "error") Assert.Equal(mode == "success" ? 20 : 0, json.GetProperty(kind).GetArrayLength());
        if (mode == "success")
        {
            var item = json.GetProperty(kind)[0];
            Assert.Equal("0", item.GetProperty("id").GetString());
            Assert.Equal(JsonValueKind.Null, item.GetProperty("artworkUrl").ValueKind);
            if (kind == "albums")
            {
                Assert.Equal("Album", item.GetProperty("title").GetString());
                Assert.Equal(JsonValueKind.Null, item.GetProperty("trackCount").ValueKind);
                Assert.Equal(JsonValueKind.Null, item.GetProperty("year").ValueKind);
            }
            else Assert.Equal("Artist", item.GetProperty("name").GetString());
        }
        metadata.VerifyAll();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Track_and_isrc_lookup_pass_the_selected_identity(bool isrc)
    {
        var metadata = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        if (isrc)
            metadata.Setup(item => item.LookupByIsrcAsync(It.IsAny<ProviderExecutionContext>(),
                    It.Is<ProviderIsrcLookupRequest>(request => request.Isrc == "USQX91300108")))
                .ReturnsAsync(ProviderOutcome<ProviderTrackMetadata>.Success(Track()));
        else
            metadata.Setup(item => item.GetTrackAsync(It.IsAny<ProviderExecutionContext>(),
                    It.Is<ProviderTrackLookupRequest>(request => request.Id.ProviderId == "fixture" &&
                        request.Id.ResourceKind == ProviderResourceKind.Track && request.Id.Value == "selected")))
                .ReturnsAsync(ProviderOutcome<ProviderTrackMetadata>.Success(Track()));

        var result = await Operations(metadata.Object).ExecuteAsync(ProviderTryOperationsTests.Context(), new()
        { Capability = "metadata", Operation = isrc ? "isrc" : "track", Id = "selected", Isrc = "USQX91300108" });

        Assert.True(result.Succeeded);
        var track = Json(result).GetProperty("tracks")[0];
        Assert.Equal("sample", track.GetProperty("id").GetString());
        Assert.Equal("Artist", track.GetProperty("artists")[0].GetString());
        Assert.Equal(JsonValueKind.Null, track.GetProperty("explicit").ValueKind);
        Assert.Equal(JsonValueKind.Null, track.GetProperty("artworkUrl").ValueKind);
        Assert.False(track.TryGetProperty("ArtworkUrl", out _));
        metadata.VerifyAll();
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("lrc")]
    [InlineData("timed")]
    [InlineData("unavailable")]
    [InlineData("restricted")]
    [InlineData("error")]
    public async Task Lyrics_pass_lookup_fields_and_project_clean_text(string mode)
    {
        ProviderLyricsRequest? observed = null;
        var lyrics = new Mock<IProviderLyricsCapability>(MockBehavior.Strict);
        lyrics.Setup(item => item.FetchLyricsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderLyricsRequest>()))
            .Callback<ProviderExecutionContext, ProviderLyricsRequest>((_, request) => observed = request)
            .ReturnsAsync(mode == "error" ? ProviderOutcome<ProviderLyricsResult>.Failure(new(ProviderErrorKind.TransientFailure)) :
                ProviderOutcome<ProviderLyricsResult>.Success(mode is "unavailable" or "restricted" ?
                    new(mode == "restricted" ? ProviderLyricsAvailabilityState.Restricted : ProviderLyricsAvailabilityState.Unavailable, "Fixture") : mode == "timed" ?
                    new(ProviderLyricsAvailabilityState.Available, "Fixture", ProviderLyricsFormat.WordTimed, "Hello world",
                        timedLines: [new("<00:01.200>Hello <00:01.500>world", TimeSpan.FromMilliseconds(1200))]) : mode == "plain" ?
                    new(ProviderLyricsAvailabilityState.Available, "Fixture", ProviderLyricsFormat.PlainText, "Hello world\r\nPlain line") :
                    new(ProviderLyricsAvailabilityState.Available, "Fixture", ProviderLyricsFormat.LineTimed,
                        "[01:02.345]<01:02.345>Hello <01:02.500>world\r\nPlain line")));

        var result = await Operations(lyrics: lyrics.Object).ExecuteAsync(ProviderTryOperationsTests.Context(), new()
        {
            Capability = "lyrics",
            Operation = "lyrics",
            TrackId = "chosen",
            Title = "Song",
            Artist = " First, , Second ,",
            Album = "Album",
            DurationSeconds = 210,
            Isrc = "USQX91300108"
        });

        Assert.NotNull(observed);
        Assert.Equal("chosen", observed.ProviderTrackId.Value);
        Assert.Equal("fixture", observed.ProviderTrackId.ProviderId);
        Assert.Equal(new[] { "First", "Second" }, observed.ArtistNames);
        Assert.Equal("Song", observed.TrackTitle);
        Assert.Equal("Album", observed.AlbumTitle);
        Assert.Equal(210, observed.DurationSeconds);
        Assert.Equal("USQX91300108", observed.Isrc);
        var json = Json(result);
        AssertEnvelope(json);
        if (mode is "unavailable" or "restricted" or "error")
        {
            Assert.False(result.Succeeded);
            Assert.Equal(mode == "error" ? "transient-failure" : mode == "restricted" ? "lyrics-restricted" : "not_found", result.ReasonCode);
            Assert.False(json.TryGetProperty("lyrics", out _));
            return;
        }
        Assert.True(result.Succeeded);
        var value = json.GetProperty("lyrics");
        Assert.Equal(mode == "timed" ? "word" : mode == "plain" ? "plain" : "line", value.GetProperty("format").GetString());
        Assert.Equal("Fixture", value.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, value.GetProperty("language").ValueKind);
        Assert.Equal(mode == "timed" ? 1 : 2, value.GetProperty("lineCount").GetInt32());
        if (mode == "plain") Assert.Equal(JsonValueKind.Null, value.GetProperty("lines")[0].GetProperty("startMs").ValueKind);
        else Assert.Equal(mode == "timed" ? 1200 : 62345, value.GetProperty("lines")[0].GetProperty("startMs").GetDouble());
        Assert.Equal("Hello world", value.GetProperty("lines")[0].GetProperty("text").GetString());
        Assert.False(value.GetProperty("lines")[0].TryGetProperty("StartMs", out _));
        if (mode != "timed") Assert.Equal(JsonValueKind.Null, value.GetProperty("lines")[1].GetProperty("startMs").ValueKind);
    }

    [Theory]
    [InlineData("list-id", "list-id")]
    [InlineData("https://music.example.test/playlists/list%20id?view=public", "list id")]
    public async Task Playlist_url_or_id_is_normalized_and_tracks_are_limited_to_one_hundred(string input, string expected)
    {
        var playlists = new Mock<IProviderPlaylistCapability>(MockBehavior.Strict);
        playlists.Setup(item => item.GetPlaylistTracksAsync(It.IsAny<ProviderExecutionContext>(),
                It.Is<ProviderPlaylistTracksRequest>(request => request.PlaylistId.ProviderId == "fixture" &&
                    request.PlaylistId.Value == expected && request.Page.Limit == 100)))
            .ReturnsAsync(ProviderOutcome<ProviderPlaylistTrackPage>.Success(new(Playlist(), new("fixture",
                Enumerable.Range(0, 105).Select(index => new ProviderPlaylistTrack(index,
                    new("fixture", ProviderResourceKind.Track, index.ToString()),
                    metadata: index == 0 ? Track("0") : null))))));

        var result = await Operations(playlists: playlists.Object).ExecuteAsync(ProviderTryOperationsTests.Context(), new()
        { Capability = "playlist", Operation = "playlist", Url = input });

        Assert.True(result.Succeeded);
        var json = Json(result);
        AssertEnvelope(json);
        Assert.Equal(100, json.GetProperty("tracks").GetArrayLength());
        Assert.Equal("Song", json.GetProperty("tracks")[0].GetProperty("title").GetString());
        var missing = json.GetProperty("tracks")[1];
        Assert.Equal("Unknown track", missing.GetProperty("title").GetString());
        Assert.Equal(0, missing.GetProperty("artists").GetArrayLength());
        foreach (var key in new[] { "album", "durationSeconds", "isrc", "explicit", "artworkUrl", "year" })
            Assert.Equal(JsonValueKind.Null, missing.GetProperty(key).ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("playlist").GetProperty("owner").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("playlist").GetProperty("trackCount").ValueKind);
        playlists.VerifyAll();
    }

    [Fact]
    public async Task Mine_accepts_an_empty_successful_page()
    {
        var playlists = new Mock<IProviderPlaylistCapability>(MockBehavior.Strict);
        playlists.Setup(item => item.GetUserPlaylistsAsync(It.IsAny<ProviderExecutionContext>(),
                It.Is<ProviderUserPlaylistsRequest>(request => request.Page.Limit == 100)))
            .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Success(new("fixture", [])));
        var result = await Operations(playlists: playlists.Object).ExecuteAsync(ProviderTryOperationsTests.Context(), new()
        { Capability = "playlist", Operation = "mine" });
        Assert.True(result.Succeeded);
        Assert.Null(result.ReasonCode);
        Assert.Equal(0, Json(result).GetProperty("playlists").GetArrayLength());
        playlists.VerifyAll();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Lyrics_probe_uses_metadata_or_the_public_sample_fields(bool metadataPresent)
    {
        var metadata = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        if (metadataPresent)
            metadata.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(),
                    It.Is<ProviderMetadataSearchRequest>(request => request.Page.Limit == 3)))
                .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("fixture", [Track()])));
        ProviderLyricsRequest? observed = null;
        var lyrics = new Mock<IProviderLyricsCapability>(MockBehavior.Strict);
        lyrics.Setup(item => item.FetchLyricsAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderLyricsRequest>()))
            .Callback<ProviderExecutionContext, ProviderLyricsRequest>((_, request) => observed = request)
            .ReturnsAsync(ProviderOutcome<ProviderLyricsResult>.Success(new(ProviderLyricsAvailabilityState.Available,
                "Fixture", ProviderLyricsFormat.PlainText, "Sample line")));
        var capabilities = new List<ProviderCapabilityDescriptor> { Capability(ProviderCapabilityKind.Lyrics, "fetchLyrics") };
        if (metadataPresent) capabilities.Add(Capability(ProviderCapabilityKind.Metadata, "searchTracks"));

        var result = await Operations(metadata.Object, lyrics.Object).ProbeAsync(ProviderTryOperationsTests.Context(),
            ProviderTryOperationsTests.Descriptor(capabilities.ToArray()), ProviderCapabilityKind.Lyrics);

        Assert.True(result.Succeeded);
        Assert.NotNull(observed);
        Assert.Equal(metadataPresent ? "sample" : "lyrics-lookup", observed.ProviderTrackId.Value);
        Assert.Contains(observed.TrackTitle, metadataPresent ? ["Song"] : ProviderDiagnosticSamples.Recordings("fixture").Select(item => item.Title));
        Assert.Contains(Assert.Single(observed.ArtistNames), metadataPresent ? ["Artist"] : ProviderDiagnosticSamples.Recordings("fixture").Select(item => item.Artist));
        Assert.Contains(observed.DurationSeconds, metadataPresent ? [210] : ProviderDiagnosticSamples.Recordings("fixture").Select(item => (int?)item.DurationSeconds));
        Assert.Equal("USQX91300108", observed.Isrc);
        Assert.Equal("plain", Json(result).GetProperty("lyrics").GetProperty("format").GetString());
        metadata.VerifyAll();
    }

    [Theory]
    [InlineData(false, ProviderDownloadAvailabilityState.Unavailable, false)]
    [InlineData(true, ProviderDownloadAvailabilityState.Available, true)]
    [InlineData(true, ProviderDownloadAvailabilityState.Unavailable, false)]
    public async Task Download_probe_checks_availability_only_when_the_hook_exists(bool checkAvailability,
        ProviderDownloadAvailabilityState state, bool success)
    {
        var metadata = new Mock<IProviderMetadataCapability>(MockBehavior.Strict);
        var download = new Mock<IProviderDownloadCapability>(MockBehavior.Strict);
        if (checkAvailability)
        {
            metadata.Setup(item => item.SearchTracksAsync(It.IsAny<ProviderExecutionContext>(), It.IsAny<ProviderMetadataSearchRequest>()))
                .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderTrackMetadata>>.Success(new("fixture", [Track()])));
            download.Setup(item => item.CheckAvailabilityAsync(It.IsAny<ProviderExecutionContext>(),
                    It.Is<ProviderDownloadAvailabilityRequest>(request => request.TrackId.Value == "sample" &&
                        request.TrackId.ProviderId == "fixture" && request.RequestedQuality == ProviderAudioQuality.Any)))
                .ReturnsAsync(ProviderOutcome<ProviderDownloadAvailability>.Success(new(state)));
        }
        var descriptor = ProviderTryOperationsTests.Descriptor(Capability(ProviderCapabilityKind.Metadata, "searchTracks"),
            Capability(ProviderCapabilityKind.Download, checkAvailability ? ["download", "checkAvailability"] : ["download"]));

        var result = await Operations(metadata.Object, download: download.Object).ProbeAsync(
            ProviderTryOperationsTests.Context(), descriptor, ProviderCapabilityKind.Download);

        Assert.False(result.Succeeded);
        Assert.Equal(success ? "download-not-verified" : checkAvailability ? "sample-unavailable" : "probe_not_available", result.ReasonCode);
        metadata.VerifyAll();
        download.VerifyAll();
        if (!checkAvailability)
        {
            metadata.VerifyNoOtherCalls();
            download.VerifyNoOtherCalls();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Playlist_probe_uses_mine_only_with_a_selected_account(bool selectedAccount)
    {
        var playlists = new Mock<IProviderPlaylistCapability>(MockBehavior.Strict);
        if (selectedAccount)
            playlists.Setup(item => item.GetUserPlaylistsAsync(It.IsAny<ProviderExecutionContext>(),
                    It.Is<ProviderUserPlaylistsRequest>(request => request.Page.Limit == 100)))
                .ReturnsAsync(ProviderOutcome<ProviderPage<ProviderPlaylistSummary>>.Success(new("fixture", [])));
        var context = ProviderTryOperationsTests.Context();
        if (selectedAccount) context = new(context.Actor, context.ProviderId,
            new ProviderAccountContext(Guid.NewGuid(), "fixture", ProviderAccountScope.Shared, 1),
            context.Policy, context.OperationId, context.CorrelationId, context.Deadline, context.CancellationToken);
        var descriptor = ProviderTryOperationsTests.Descriptor(Capability(ProviderCapabilityKind.Playlist, "getUserPlaylists", "getPlaylistTracks"));

        var result = await Operations(playlists: playlists.Object).ProbeAsync(context, descriptor, ProviderCapabilityKind.Playlist);

        Assert.Equal(selectedAccount, result.Succeeded);
        Assert.Equal(selectedAccount ? null : "probe_not_available", result.ReasonCode);
        Assert.Equal(selectedAccount, Json(result).TryGetProperty("playlists", out _));
        playlists.VerifyAll();
        if (!selectedAccount) playlists.VerifyNoOtherCalls();
    }

    private static ProviderTryOperations Operations(IProviderMetadataCapability? metadata = null,
        IProviderLyricsCapability? lyrics = null, IProviderPlaylistCapability? playlists = null,
        IProviderDownloadCapability? download = null)
    {
        var registry = new Mock<IProviderRegistry>(MockBehavior.Strict);
        if (metadata != null) registry.Setup(item => item.GetRequiredCapability<IProviderMetadataCapability>("fixture", ProviderCapabilityKind.Metadata)).Returns(metadata);
        if (lyrics != null) registry.Setup(item => item.GetRequiredCapability<IProviderLyricsCapability>("fixture", ProviderCapabilityKind.Lyrics)).Returns(lyrics);
        if (playlists != null) registry.Setup(item => item.GetRequiredCapability<IProviderPlaylistCapability>("fixture", ProviderCapabilityKind.Playlist)).Returns(playlists);
        if (download != null) registry.Setup(item => item.GetRequiredCapability<IProviderDownloadCapability>("fixture", ProviderCapabilityKind.Download)).Returns(download);
        return new(registry.Object);
    }

    private static ProviderCapabilityDescriptor Capability(ProviderCapabilityKind kind, params string[] hooks) =>
        new(kind, ProviderCapabilitySupportState.Supported, ProviderAccountRequirement.None, "1", hooks);

    private static ProviderTrackMetadata Track(string id = "sample") => new(new("fixture", ProviderResourceKind.Track, id),
        "Song", [new("Artist")], albumTitle: "Album", duration: TimeSpan.FromSeconds(210), isrc: "USQX91300108");

    private static ProviderPlaylistSummary Playlist() => new(new("fixture", ProviderResourceKind.Playlist, "list"),
        "Playlist", new("owner"), "revision");

    private static JsonElement Json(ProviderTryResult result) => JsonSerializer.SerializeToElement(result,
        new JsonSerializerOptions { PropertyNamingPolicy = null });

    private static void AssertEnvelope(JsonElement json)
    {
        foreach (var key in new[] { "provider", "capability", "operation", "accountId", "succeeded", "elapsedMs", "reasonCode", "message" })
            Assert.True(json.TryGetProperty(key, out _), key);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("accountId").ValueKind);
        if (json.GetProperty("succeeded").GetBoolean())
        {
            Assert.Equal(JsonValueKind.Null, json.GetProperty("reasonCode").ValueKind);
            Assert.Equal(JsonValueKind.Null, json.GetProperty("message").ValueKind);
        }
        Assert.False(json.TryGetProperty("Succeeded", out _));
    }
}
