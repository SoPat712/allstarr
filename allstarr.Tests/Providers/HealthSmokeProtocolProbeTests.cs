using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using allstarr.Core.Health;
using Microsoft.AspNetCore.WebUtilities;

namespace allstarr.Tests;

public sealed class HealthSmokeProtocolProbeTests
{
    [Theory]
    [InlineData("Jellyfin")]
    [InlineData("Subsonic")]
    public async Task EveryProtocolStepIsCheckedAndTemporaryStateIsRemoved(string protocol)
    {
        using var upstream = new Fixture(protocol);
        var result = await upstream.RunAsync();
        Assert.Equal(HealthSmokeProtocolProbe.StepNames, result.Select(step => step.Name));
        Assert.All(result, step => Assert.Equal("passed", step.Status));
        Assert.False(upstream.Hearted);
        Assert.False(upstream.PlaylistExists);
        Assert.True(upstream.SeekStart > 8191);
        Assert.True(upstream.DownloadRequested);
        Assert.DoesNotContain("fixture-session", JsonSerializer.Serialize(result));
        Assert.All(upstream.Requests, uri => Assert.DoesNotContain("fixture-session", uri));
    }

    [Theory]
    [InlineData("Jellyfin", "search")]
    [InlineData("Subsonic", "search")]
    [InlineData("Jellyfin", "playback")]
    [InlineData("Subsonic", "seek")]
    [InlineData("Jellyfin", "now-playing")]
    [InlineData("Subsonic", "scrobble")]
    [InlineData("Jellyfin", "heart")]
    [InlineData("Subsonic", "unheart")]
    [InlineData("Jellyfin", "playlist-create")]
    [InlineData("Subsonic", "playlist-add")]
    [InlineData("Jellyfin", "playlist-remove")]
    [InlineData("Subsonic", "playlist-delete")]
    [InlineData("Jellyfin", "download")]
    [InlineData("Subsonic", "lyrics")]
    public async Task EachFailedStepHasASafeReasonAndStillCleansUp(string protocol, string step)
    {
        using var upstream = new Fixture(protocol) { FailStep = step };
        var result = await upstream.RunAsync();
        Assert.Contains(result, item => item.Name == step && item.Status == "failed" && item.Reason == "http_503");
        Assert.False(upstream.Hearted);
        Assert.False(upstream.PlaylistExists);
        Assert.DoesNotContain("private-upstream-body", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("Jellyfin")]
    [InlineData("Subsonic")]
    public async Task AnExistingHeartIsRestored(string protocol)
    {
        using var upstream = new Fixture(protocol) { Hearted = true };
        Assert.All(await upstream.RunAsync(), step => Assert.Equal("passed", step.Status));
        Assert.True(upstream.Hearted);
        Assert.False(upstream.PlaylistExists);
    }

    [Fact]
    public async Task HtmlDisguisedAsAudioIsRejected()
    {
        using var upstream = new Fixture("Jellyfin") { HtmlAudio = true };
        Assert.Contains(await upstream.RunAsync(), step => step.Name == "playback" && step.Status == "failed" && step.Reason == "invalid_audio");
    }

    [Fact]
    public async Task ManualOnlySearchResultsAreNeverSelected()
    {
        using var upstream = new Fixture("Jellyfin");
        var result = await upstream.RunAsync(eligible: _ => false);
        Assert.Contains(result, step => step.Name == "search" && step.Status == "failed" && step.Reason == "no_eligible_song");
        Assert.False(upstream.DownloadRequested);
        Assert.Null(upstream.SeekStart);
    }

    [Fact]
    public async Task CancelledChecksStillRestoreTheHeartAndDeleteTheirPlaylist()
    {
        using var upstream = new Fixture("Jellyfin") { StallStep = "playlist-add" };
        using var cancellation = new CancellationTokenSource();
        var run = upstream.RunAsync(cancellation: cancellation.Token);
        await upstream.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.False(upstream.Hearted);
        Assert.False(upstream.PlaylistExists);
    }

    [Fact]
    public async Task AStalledFirstByteIsBoundedByThePlaybackBudget()
    {
        using var upstream = new Fixture("Jellyfin") { StallStep = "playback" };
        var result = await upstream.RunAsync();
        var step = Assert.Single(result, item => item.Name == "playback");
        Assert.Equal("failed", step.Status);
        Assert.Equal("timeout", step.Reason);
        Assert.InRange(step.LatencyMilliseconds, 1000, 5000);
        Assert.False(upstream.PlaylistExists);
    }

    [Theory]
    [InlineData("Jellyfin")]
    [InlineData("Subsonic")]
    public async Task RecoveryAcceptsAnAlreadyDeletedPlaylist(string protocol)
    {
        using var upstream = new Fixture(protocol) { MissingPlaylistIsNotFound = true, Hearted = true };
        var remaining = await upstream.CleanupAsync(new("ext-deezer-song-fixture", false, "fixture-playlist", $"[allstarr-test] Health {Fixture.CleanupRunId:N}"));
        Assert.Equal(new HealthSmokeArtifacts(), remaining);
        Assert.False(upstream.Hearted);
    }

    [Theory]
    [InlineData("Jellyfin", "http_404")]
    [InlineData("Subsonic", "protocol_error_70")]
    public async Task TheDeleteCheckStillDetectsAnUnexpectedlyMissingPlaylist(string protocol, string reason)
    {
        using var upstream = new Fixture(protocol) { MissingPlaylistIsNotFound = true, LosePlaylistBeforeDelete = true };
        var result = await upstream.RunAsync();
        Assert.Contains(result, step => step.Name == "playlist-delete" && step.Status == "failed" && step.Reason == reason);
        Assert.Contains(result, step => step.Name == "cleanup" && step.Status == "passed");
    }

    [Theory]
    [InlineData("Jellyfin")]
    [InlineData("Subsonic")]
    public async Task ThePlaybackBudgetMeasuresFirstAudioRatherThanTheWholePrefix(string protocol)
    {
        using var upstream = new Fixture(protocol) { SlowAudioPrefix = true };
        var result = await upstream.RunAsync();
        var playback = Assert.Single(result, step => step.Name == "playback");
        Assert.Equal("passed", playback.Status);
        Assert.InRange(playback.LatencyMilliseconds, 0, 1500);
        Assert.All(result, step => Assert.Equal("passed", step.Status));
    }

    [Theory]
    [InlineData("Jellyfin")]
    [InlineData("Subsonic")]
    public async Task MissingPlaylistEnvelopeCannotProveRemovalOrSuccessfulCleanup(string protocol)
    {
        using var upstream = new Fixture(protocol) { MissingPlaylistEnvelope = true };
        var results = await upstream.RunAsync();
        Assert.Contains(results, step => step.Name == "playlist-remove" && step.Status == "failed" && step.Reason == "invalid_response");
        await Assert.ThrowsAnyAsync<Exception>(() => upstream.CleanupAsync(new(PlaylistName:
            $"[allstarr-test] Health {Fixture.CleanupRunId:N}")));
    }

    [Theory]
    [InlineData("Jellyfin")]
    [InlineData("Subsonic")]
    public async Task MissingFavoriteDetailsNeverReplaceAnUnknownOriginalHeart(string protocol)
    {
        using var upstream = new Fixture(protocol) { MissingHeartDetails = true, Hearted = true };
        var results = await upstream.RunAsync();
        Assert.Contains(results, step => step.Name == "heart" && step.Status == "failed" && step.Reason == "invalid_response");
        Assert.True(upstream.Hearted);
    }

    [Fact]
    public async Task CleanupRejectsNativeItemArtifactsBeforeAnyMutation()
    {
        using var upstream = new Fixture("Jellyfin") { Hearted = true };
        await Assert.ThrowsAnyAsync<Exception>(() => upstream.CleanupAsync(new("native-track", false)));
        Assert.True(upstream.Hearted);
        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public async Task CleanupRejectsAPlaylistOutsideTheRunName()
    {
        using var upstream = new Fixture("Jellyfin");
        await Assert.ThrowsAnyAsync<Exception>(() => upstream.CleanupAsync(new(PlaylistId: "fixture-playlist", PlaylistName: "Household playlist")));
        Assert.Empty(upstream.Requests);
    }

    internal sealed class Fixture(string protocol) : HttpMessageHandler
    {
        public static readonly Guid CleanupRunId = Guid.Parse("018f1f6e-8e9c-77f5-9a79-3d8a494d60cd");
        private const string Item = "ext-deezer-song-fixture";
        public List<string> Requests { get; } = [];
        public string? FailStep { get; set; }
        public string? StallStep { get; init; }
        public TaskCompletionSource<bool> Stalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HtmlAudio { get; set; }
        public bool MissingPlaylistEnvelope { get; init; }
        public bool MissingHeartDetails { get; init; }
        public bool FailCleanup { get; set; }
        public bool SlowAudioPrefix { get; init; }
        public bool MissingPlaylistIsNotFound { get; init; }
        public bool LosePlaylistBeforeDelete { get; init; }
        public bool Hearted { get; set; }
        public bool PlaylistExists { get; private set; }
        public bool DownloadRequested { get; private set; }
        public long? SeekStart { get; private set; }
        private bool _failed;
        private bool _songInPlaylist;

        public void FailNextRunAt(string? step) { FailStep = step; _failed = false; }

        public async Task<IReadOnlyList<HealthSmokeStep>> RunAsync(Func<string, bool>? eligible = null, CancellationToken cancellation = default)
        {
            using var client = new HttpClient(this, disposeHandler: false) { BaseAddress = new("http://fixture/") };
            var probe = new HealthSmokeProtocolProbe(client, TimeProvider.System);
            return await probe.RunAsync(new(protocol, Guid.NewGuid(), "fixture-principal", "Fixture song", 2,
                new HealthSmokeCredentials { UserName = "fixture-listener", Password = "fixture-session", AccessToken = "fixture-session" },
                eligible ?? (_ => true), (_, _) => Task.CompletedTask), cancellation);
        }

        public async Task<HealthSmokeArtifacts?> CleanupAsync(HealthSmokeArtifacts artifacts)
        {
            using var client = new HttpClient(this, disposeHandler: false) { BaseAddress = new("http://fixture/") };
            HealthSmokeArtifacts? remaining = null;
            await new HealthSmokeProtocolProbe(client, TimeProvider.System).CleanupAsync(new(protocol, CleanupRunId,
                "fixture-principal", "Fixture song", 2, new() { UserName = "fixture-listener", AccessToken = "fixture-session" },
                _ => true, (saved, _) => { remaining = saved; return Task.CompletedTask; }), artifacts, default);
            return remaining;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri.ToString());
            var path = uri.AbsolutePath.Trim('/');
            var fields = QueryHelpers.ParseQuery(uri.Query).ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            if (request.Content?.Headers.ContentType?.MediaType == "application/x-www-form-urlencoded")
                foreach (var pair in QueryHelpers.ParseQuery("?" + await request.Content.ReadAsStringAsync(cancellationToken)))
                    fields[pair.Key] = pair.Value.ToString();
            var body = request.Content?.Headers.ContentType?.MediaType == "application/json"
                ? await request.Content.ReadAsStringAsync(cancellationToken) : "{}";
            var operation = path.Split('/').Last().Replace(".view", "", StringComparison.Ordinal);
            string step;
            if (MissingPlaylistEnvelope && (path == "Items" && fields.GetValueOrDefault("IncludeItemTypes") == "Playlist" ||
                    operation is "getPlaylists" or "getPlaylist" || path.StartsWith("Playlists/", StringComparison.Ordinal) && request.Method == HttpMethod.Get))
                return protocol == "Jellyfin" ? Json(new { }) : Sub(new { });
            if (path == "Items" && fields.GetValueOrDefault("IncludeItemTypes") == "Playlist")
                return Json(new { Items = PlaylistExists ? new[] { new { Id = "fixture-playlist", Name = fields.GetValueOrDefault("SearchTerm"), Type = "Playlist" } } : [] });
            if (operation == "getPlaylists")
                return Sub(new { playlists = new { playlist = PlaylistExists ? new[] { new { id = "fixture-playlist", name = fields.GetValueOrDefault("query") } } : [] } });
            if (path == "Items" || operation == "search3") step = "search";
            else if (path.Contains("/Download", StringComparison.Ordinal) || operation == "download") step = "download";
            else if (path.EndsWith("/Lyrics", StringComparison.Ordinal) || operation == "getLyricsBySongId") step = "lyrics";
            else if (path.StartsWith("Audio/", StringComparison.Ordinal) || operation == "stream")
                step = request.Headers.Range?.Ranges.First().From > 0 ? "seek" : "playback";
            else if (path == "Sessions/Playing") step = "now-playing";
            else if (path == "Sessions/Playing/Stopped") step = "scrobble";
            else if (operation == "scrobble") step = fields.GetValueOrDefault("submission") == "false" ? "now-playing" : "scrobble";
            else if (path.Contains("FavoriteItems", StringComparison.Ordinal) || operation is "star" or "unstar")
                step = request.Method == HttpMethod.Delete || operation == "unstar" ? "unheart" : "heart";
            else if (path == "Playlists" || operation == "createPlaylist") step = "playlist-create";
            else if (path.StartsWith("Items/fixture-playlist", StringComparison.Ordinal) || operation == "deletePlaylist") step = "playlist-delete";
            else if ((path.StartsWith("Playlists/", StringComparison.Ordinal) && request.Method == HttpMethod.Get) || operation == "getPlaylist")
                return protocol == "Jellyfin"
                    ? Json(new { Items = _songInPlaylist ? new[] { new { Id = Item, PlaylistItemId = "fixture-entry" } } : [] })
                    : Sub(new { playlist = new { id = "fixture-playlist", entry = _songInPlaylist ? new[] { new { id = Item } } : [] } });
            else if (path.StartsWith("Playlists/", StringComparison.Ordinal) || operation == "updatePlaylist")
                step = request.Method == HttpMethod.Delete || fields.ContainsKey("songIndexToRemove") ? "playlist-remove" : "playlist-add";
            else if (operation == "getSong" || path.Contains("/Items/", StringComparison.Ordinal))
                return MissingHeartDetails ? protocol == "Jellyfin" ? Json(new { }) : Sub(new { }) : protocol == "Jellyfin" ? Json(new { UserData = new { IsFavorite = Hearted }, RunTimeTicks = TimeSpan.FromMinutes(4).Ticks })
                    : Sub(new { song = new Dictionary<string, object?> { ["id"] = Item, ["duration"] = 240, ["starred"] = Hearted ? "2026-01-01" : null } });
            else throw new InvalidOperationException("Unexpected fixture route: " + path);
            if (FailCleanup && step == "unheart" && !PlaylistExists && _songInPlaylist == false)
                return new(HttpStatusCode.ServiceUnavailable);
            if (step == FailStep && !_failed)
            {
                _failed = true;
                return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("private-upstream-body") };
            }
            if (step == StallStep)
            {
                Stalled.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            switch (step)
            {
                case "search":
                    return protocol == "Jellyfin"
                    ? Json(new { Items = new[] { new { Id = Item, Name = "Fixture song", Type = "Audio" } } })
                    : Sub(new { searchResult3 = new { song = new[] { new { id = Item, title = "Fixture song" } } } });
                case "playback":
                case "seek":
                case "download":
                    if (step == "seek") SeekStart = request.Headers.Range!.Ranges.First().From;
                    if (step == "download") DownloadRequested = true;
                    var start = request.Headers.Range?.Ranges.First().From ?? 0;
                    var audio = new HttpResponseMessage(HttpStatusCode.PartialContent)
                    {
                        Content = SlowAudioPrefix && step == "playback" ? new StreamContent(new SlowPrefixStream())
                        : new ByteArrayContent(HtmlAudio ? Encoding.UTF8.GetBytes("<html>busy</html>") : new byte[8192])
                    };
                    audio.Content.Headers.ContentType = new("audio/flac");
                    audio.Content.Headers.ContentRange = new(start, start + 8191, 1_000_000);
                    return audio;
                case "heart": Hearted = true; break;
                case "unheart": Hearted = false; break;
                case "playlist-create": PlaylistExists = true; return protocol == "Jellyfin" ? Json(new { Id = "fixture-playlist" }) : Sub(new { playlist = new { id = "fixture-playlist" } });
                case "playlist-add": _songInPlaylist = true; break;
                case "playlist-remove": _songInPlaylist = false; break;
                case "playlist-delete":
                    if (LosePlaylistBeforeDelete) PlaylistExists = false;
                    if (!PlaylistExists && MissingPlaylistIsNotFound)
                        return protocol == "Jellyfin" ? new(HttpStatusCode.NotFound)
                            : MissingSubsonicPlaylist();
                    PlaylistExists = false;
                    break;
                case "lyrics":
                    return protocol == "Jellyfin" ? Json(new { Lyrics = new[] { new { Text = "Fixture line" } } })
                    : Sub(new { lyricsList = new { structuredLyrics = new[] { new { line = new[] { new { value = "Fixture line" } } } } } });
            }
            return protocol == "Jellyfin" ? new(HttpStatusCode.NoContent) : Sub(new { });
        }

        private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        private static HttpResponseMessage Sub(object body)
        {
            var values = JsonSerializer.SerializeToNode(body)!.AsObject();
            values["status"] = "ok";
            return Json(new Dictionary<string, object> { ["subsonic-response"] = values });
        }

        private static HttpResponseMessage MissingSubsonicPlaylist() => Json(new Dictionary<string, object>
        { ["subsonic-response"] = new { status = "failed", error = new { code = 70, message = "Not found" } } });

        private sealed class SlowPrefixStream() : MemoryStream(new byte[8192])
        {
            private bool _started;
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (!_started) { _started = true; return await base.ReadAsync(buffer[..1], cancellationToken); }
                await Task.Delay(TimeSpan.FromMilliseconds(2500), cancellationToken);
                return await base.ReadAsync(buffer, cancellationToken);
            }
        }
    }
}
