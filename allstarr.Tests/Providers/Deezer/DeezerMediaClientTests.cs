using System.Net;
using System.Text.Json;
using allstarr.Core.Providers.Deezer;
using allstarr.Services.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class DeezerMediaClientTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task ReadableOriginal_PreservesOriginalIdAndToken(bool includeId, bool includeReadable)
    {
        var calls = new List<string>();
        using var fixture = new Fixture(request =>
        {
            calls.Add(request.RequestUri!.PathAndQuery);
            if (IsAuth(request)) return Auth();
            if (request.RequestUri.AbsolutePath == "/track/42")
                return Track(includeId ? "42" : null, "original-token", includeReadable ? true : null);
            Assert.Equal("/v1/get_url", request.RequestUri.AbsolutePath);
            using var body = Body(request);
            Assert.Equal("original-token", body.RootElement.GetProperty("track_tokens")[0].GetString());
            Assert.False(request.Headers.Contains("Cookie"));
            return Media();
        });

        var result = await fixture.Client.ResolveDownloadAsync("42", "account-a", null, "FLAC", CancellationToken.None);

        Assert.Equal("42", result.TrackId);
        Assert.Equal("FLAC", result.Format);
        Assert.Equal("Track", result.Title);
        Assert.Equal("Artist", result.Artist);
        Assert.Equal(3, calls.Count);
        Assert.DoesNotContain(calls, path => path.Contains("pageTrack", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, "original-token")]
    [InlineData(true, null)]
    public async Task PublicAlternative_IsValidatedAndUsesSelectedSessionToken(bool readable, string? originalToken)
    {
        var fetchedTracks = new List<string>();
        using var fixture = new Fixture(request =>
        {
            if (IsAuth(request)) return Auth();
            if (request.RequestUri!.AbsolutePath == "/track/42")
            {
                fetchedTracks.Add("42");
                return Track("42", originalToken, readable, "43");
            }
            if (request.RequestUri.AbsolutePath == "/track/43")
            {
                fetchedTracks.Add("43");
                return Track("43", "candidate-public-token", true);
            }
            if (IsPage(request))
            {
                using var pageBody = Body(request);
                Assert.Equal("43", pageBody.RootElement.GetProperty("SNG_ID").GetString());
                Assert.Contains("api_token=form", request.RequestUri.Query);
                return Page("43", token: "candidate-session-token");
            }
            Assert.Equal("/v1/get_url", request.RequestUri.AbsolutePath);
            using var body = Body(request);
            Assert.Equal("candidate-session-token", body.RootElement.GetProperty("track_tokens")[0].GetString());
            return Media();
        });

        var result = await fixture.Client.ResolveDownloadAsync("42", "account-a", null, "FLAC", CancellationToken.None);

        Assert.Equal("43", result.TrackId);
        Assert.Equal(new[] { "42", "43" }, fetchedTracks);
    }

    [Fact]
    public async Task PrivateFallback_IsFetchedAndVerifiedBeforeMediaLookup()
    {
        using var fixture = new Fixture(request =>
        {
            if (IsAuth(request)) return Auth();
            if (request.RequestUri!.AbsolutePath == "/track/42") return Track("42", "original", false);
            if (request.RequestUri.AbsolutePath == "/track/43") return Track("43", "selected", true);
            if (IsPage(request))
            {
                using var body = Body(request);
                return body.RootElement.GetProperty("SNG_ID").GetString() == "42"
                    ? Page("42", fallbackId: "43") : Page("43", token: "selected-private");
            }
            Assert.Equal("/v1/get_url", request.RequestUri.AbsolutePath);
            using var media = Body(request);
            Assert.Equal("selected-private", media.RootElement.GetProperty("track_tokens")[0].GetString());
            return Media();
        });

        var result = await fixture.Client.ResolveDownloadAsync("42", "account-a", null, "FLAC", CancellationToken.None);

        Assert.Equal("43", result.TrackId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EmptyOriginalMedia_TriesOneValidatedAlternativeWithoutChaining(bool alternativeHasMedia)
    {
        var mediaTokens = new List<string>();
        using var fixture = new Fixture(request =>
        {
            if (IsAuth(request)) return Auth();
            if (request.RequestUri!.AbsolutePath == "/track/42") return Track("42", "original", true, "43");
            if (request.RequestUri.AbsolutePath == "/track/43") return Track("43", "selected", true, "44");
            if (IsPage(request))
            {
                using var page = Body(request);
                Assert.Equal("43", page.RootElement.GetProperty("SNG_ID").GetString());
                return Page("43", token: "selected-private");
            }
            Assert.Equal("/v1/get_url", request.RequestUri.AbsolutePath);
            using var body = Body(request);
            var token = body.RootElement.GetProperty("track_tokens")[0].GetString()!;
            mediaTokens.Add(token);
            return token == "selected-private" && alternativeHasMedia ? Media() : Json("{\"data\":[{}]}");
        });

        if (alternativeHasMedia)
        {
            var result = await fixture.Client.ResolveDownloadAsync("42", "account-a", null, "FLAC", CancellationToken.None);
            Assert.Equal("43", result.TrackId);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
                fixture.Client.ResolveDownloadAsync("42", "account-a", null, "FLAC", CancellationToken.None));
            Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        }
        Assert.Equal(new[] { "original", "selected-private" }, mediaTokens);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("42", null)]
    [InlineData("0", null)]
    [InlineData("-43", null)]
    [InlineData("invalid", null)]
    [InlineData("43", "{\"id\":43,\"readable\":false,\"alternative\":{\"id\":42},\"track_token\":\"token\"}")]
    [InlineData("43", "{\"readable\":true,\"track_token\":\"token\"}")]
    [InlineData("43", "{\"id\":44,\"readable\":true,\"track_token\":\"token\"}")]
    [InlineData("43", "{\"id\":43,\"readable\":true}")]
    public async Task InvalidAlternative_DoesNotReachMediaOrFollowCycles(string? alternativeId, string? candidateJson)
    {
        var trackCalls = 0;
        var pageCalls = 0;
        using var fixture = new Fixture(request =>
        {
            if (IsAuth(request)) return Auth();
            if (request.RequestUri!.AbsolutePath == "/track/42")
            {
                trackCalls++;
                return Track("42", "original", false, alternativeId);
            }
            if (request.RequestUri.AbsolutePath == "/track/43")
            {
                trackCalls++;
                Assert.NotNull(candidateJson);
                return Json(candidateJson);
            }
            Assert.True(IsPage(request), "Invalid candidates must never reach media or fuzzy search.");
            pageCalls++;
            return Page("42", fallbackId: "42");
        });

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Client.ResolveDownloadAsync("42", "account-a", null, "FLAC", CancellationToken.None));

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        Assert.InRange(trackCalls, 1, 2);
        Assert.InRange(pageCalls, 1, 2);
    }

    [Theory]
    [InlineData("ISRC-ORIGINAL", "ISRC-ORIGINAL", true)]
    [InlineData("OTHER", "ISRC-ORIGINAL", false)]
    [InlineData("ISRC-ORIGINAL", "OTHER", false)]
    public async Task IsrcAlternative_RequiresSameIsrcAtLookupAndFetchedTrack(
        string lookupIsrc, string fetchedIsrc, bool expectedSuccess)
    {
        var mediaCalls = 0;
        using var fixture = new Fixture(request =>
        {
            if (IsAuth(request)) return Auth();
            if (request.RequestUri!.AbsolutePath == "/track/42")
                return Track("42", "original", false, isrc: "ISRC-ORIGINAL");
            if (request.RequestUri.AbsolutePath == "/track/isrc:ISRC-ORIGINAL")
                return Track("43", "lookup", true, isrc: lookupIsrc);
            if (request.RequestUri.AbsolutePath == "/track/43")
                return Track("43", "selected", true, isrc: fetchedIsrc);
            if (IsPage(request))
            {
                using var body = Body(request);
                var id = body.RootElement.GetProperty("SNG_ID").GetString()!;
                return Page(id);
            }
            Assert.Equal("/v1/get_url", request.RequestUri.AbsolutePath);
            mediaCalls++;
            return Media();
        });

        if (expectedSuccess)
        {
            var result = await fixture.Client.ResolveDownloadAsync("42", "account-a", null, "FLAC", CancellationToken.None);
            Assert.Equal("43", result.TrackId);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
                fixture.Client.ResolveDownloadAsync("42", "account-a", null, "FLAC", CancellationToken.None));
            Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        }
        Assert.Equal(expectedSuccess ? 1 : 0, mediaCalls);
    }

    [Fact]
    public async Task WrongOriginalId_IsRejectedBeforeMediaLookup()
    {
        using var fixture = new Fixture(request => IsAuth(request) ? Auth() : Track("99", "wrong-token", true));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Client.ResolveDownloadAsync("42", "account-a", null, "FLAC", CancellationToken.None));

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
    }

    [Theory]
    [InlineData("FLAC", "FLAC", "FLAC,MP3_320,MP3_128")]
    [InlineData("MP3_320", "MP3_320", "MP3_320,MP3_128")]
    [InlineData("MP3_128", "MP3_128", "MP3_128")]
    public async Task QualityCeiling_AppliesToRequestAndUnexpectedHigherFormats(
        string quality, string expectedFormat, string expectedRequestedFormats)
    {
        using var fixture = new Fixture(request =>
        {
            if (IsAuth(request)) return Auth();
            if (request.RequestUri!.AbsolutePath == "/track/42") return Track("42", "token", true);
            using var body = Body(request);
            var formats = body.RootElement.GetProperty("media")[0].GetProperty("formats").EnumerateArray();
            Assert.Equal(expectedRequestedFormats, string.Join(",", formats.Select(item => item.GetProperty("format").GetString())));
            return Media("FLAC", "MP3_320", "MP3_128");
        });

        var result = await fixture.Client.ResolveDownloadAsync("42", "account-a", null, quality, CancellationToken.None);

        Assert.Equal(expectedFormat, result.Format);
    }

    [Fact]
    public async Task OnlyHigherQualityReturned_IsUnavailable()
    {
        using var fixture = new Fixture(request => IsAuth(request) ? Auth() :
            request.RequestUri!.AbsolutePath == "/track/42" ? Track("42", "token", true) : Media("FLAC"));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Client.ResolveDownloadAsync("42", "account-a", null, "MP3_128", CancellationToken.None));

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task MissingCredential_IsUnauthorizedWithoutRequests(string? arl)
    {
        using var fixture = new Fixture(_ => throw new InvalidOperationException("No request expected."));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Client.ResolveDownloadAsync("42", arl, null, "FLAC", CancellationToken.None));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
    }

    [Fact]
    public async Task InvalidCredentialResponse_IsUnauthorized()
    {
        using var fixture = new Fixture(_ => Json("{\"results\":{}}"));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Client.ResolveDownloadAsync("42", "account-a", null, "FLAC", CancellationToken.None));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
    }

    [Fact]
    public async Task ConcurrentCalls_KeepCredentialsAndSessionTokensSeparate()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var authArrivals = 0;
        var bothAuthenticated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mediaCalls = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var fixture = new Fixture(async (request, cancellationToken) =>
        {
            if (IsAuth(request))
            {
                var cookie = request.Headers.GetValues("Cookie").Single();
                if (Interlocked.Increment(ref authArrivals) == 2) bothAuthenticated.SetResult();
                await bothAuthenticated.Task.WaitAsync(cancellationToken);
                return Auth(cookie);
            }
            if (request.RequestUri!.AbsolutePath == "/track/42")
            {
                Assert.False(request.Headers.Contains("Cookie"));
                return Track("42", "public", true);
            }
            Assert.False(request.Headers.Contains("Cookie"));
            using var body = Body(request);
            var credential = body.RootElement.GetProperty("license_token").GetString()!;
            Assert.Contains(credential, new[] { "arl=account-a", "arl=account-b" });
            mediaCalls.Add(credential);
            return Media();
        });

        await Task.WhenAll(
            fixture.Client.ResolveDownloadAsync("42", "account-a", null, "FLAC", cancellation.Token),
            fixture.Client.ResolveDownloadAsync("42", "account-b", null, "FLAC", cancellation.Token));

        Assert.Equal(new[] { "arl=account-a", "arl=account-b" }, mediaCalls.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task FallbackCredential_IsTriedAtMostOnce()
    {
        var credentials = new List<string>();
        using var fixture = new Fixture(request =>
        {
            Assert.True(IsAuth(request));
            credentials.Add(request.Headers.GetValues("Cookie").Single());
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Client.ResolveDownloadAsync("42", "account-a", "account-b", "FLAC", CancellationToken.None));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Equal(new[] { "arl=account-a", "arl=account-b" }, credentials);
    }

    [Fact]
    public async Task FallbackCredential_UsesItsOwnLicenseToken()
    {
        var credentials = new List<string>();
        using var fixture = new Fixture(request =>
        {
            if (IsAuth(request))
            {
                var cookie = request.Headers.GetValues("Cookie").Single();
                credentials.Add(cookie);
                return cookie == "arl=account-a" ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : Auth("fallback-license");
            }
            if (request.RequestUri!.AbsolutePath == "/track/42") return Track("42", "original", true);
            using var body = Body(request);
            Assert.Equal("fallback-license", body.RootElement.GetProperty("license_token").GetString());
            Assert.False(request.Headers.Contains("Cookie"));
            return Media();
        });

        var result = await fixture.Client.ResolveDownloadAsync("42", "account-a", "account-b", "FLAC", CancellationToken.None);

        Assert.Equal("42", result.TrackId);
        Assert.Equal(new[] { "arl=account-a", "arl=account-b" }, credentials);
    }

    [Fact]
    public async Task Throttling_DoesNotTryAnotherCredential()
    {
        var credentials = new List<string>();
        using var fixture = new Fixture(request =>
        {
            credentials.Add(request.Headers.GetValues("Cookie").Single());
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        });

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Client.ResolveDownloadAsync("42", "account-a", "account-b", "FLAC", CancellationToken.None));

        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.Equal(4, credentials.Count);
        Assert.All(credentials, credential => Assert.Equal("arl=account-a", credential));
    }

    [Fact]
    public async Task Cancellation_DoesNotTryFallbackCredential()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        using var fixture = new Fixture(_ =>
        {
            calls++;
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Client.ResolveDownloadAsync("42", "account-a", "account-b", "FLAC", cancellation.Token));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task DecryptDownload_CopiesUnencryptedTailAndLeavesCallerStreamsOpen()
    {
        using var fixture = new Fixture(_ => throw new InvalidOperationException("No request expected."));
        var bytes = Enumerable.Range(0, 127).Select(value => (byte)value).ToArray();
        using var input = new MemoryStream(bytes);
        using var output = new MemoryStream();

        await fixture.Client.DecryptDownloadAsync(input, output, "42", CancellationToken.None);

        Assert.Equal(bytes, output.ToArray());
        Assert.True(input.CanRead);
        Assert.True(output.CanWrite);
    }

    private static bool IsAuth(HttpRequestMessage request) => request.RequestUri!.Query.Contains("deezer.getUserData", StringComparison.Ordinal);
    private static bool IsPage(HttpRequestMessage request) => request.RequestUri!.Query.Contains("deezer.pageTrack", StringComparison.Ordinal);
    private static JsonDocument Body(HttpRequestMessage request) => JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private static HttpResponseMessage Auth(string license = "license") => Json(JsonSerializer.Serialize(new
    {
        results = new { checkForm = "form", USER = new { OPTIONS = new { license_token = license } } }
    }));
    private static HttpResponseMessage Track(string? id, string? token, bool? readable, string? alternativeId = null, string? isrc = null)
    {
        var data = new Dictionary<string, object?> { ["title"] = "Track", ["artist"] = new { name = "Artist" } };
        if (id != null) data["id"] = id;
        if (token != null) data["track_token"] = token;
        if (readable != null) data["readable"] = readable;
        if (alternativeId != null) data["alternative"] = new { id = alternativeId };
        if (isrc != null) data["isrc"] = isrc;
        return Json(JsonSerializer.Serialize(data));
    }
    private static HttpResponseMessage Page(string id, string? fallbackId = null, string? token = null) => Json(JsonSerializer.Serialize(new
    {
        results = new { DATA = new { SNG_ID = id, FALLBACK = new { SNG_ID = fallbackId }, TRACK_TOKEN = token } }
    }));
    private static HttpResponseMessage Media(params string[] formats) => Json(JsonSerializer.Serialize(new
    {
        data = new[] { new { media = (formats.Length == 0 ? ["FLAC"] : formats).Select(format =>
            new { format, sources = new[] { new { url = "https://media.example.test/audio" } } }).ToArray() } }
    }));

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient _http;
        public DeezerMediaClient Client { get; }
        public Fixture(Func<HttpRequestMessage, HttpResponseMessage> respond)
            : this((request, _) => Task.FromResult(respond(request))) { }
        public Fixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        {
            _http = new HttpClient(new Handler(respond));
            Client = new DeezerMediaClient(new DeezerHttpClient(_http, 0, (_, token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }), NullLogger<DeezerMediaClient>.Instance);
        }
        public void Dispose() => _http.Dispose();
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }
}

public sealed class PathHelperTests : IDisposable
{
    private readonly string _testPath;

    public PathHelperTests()
    {
        _testPath = Path.Combine(Path.GetTempPath(), "allstarr-pathhelper-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_testPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testPath))
        {
            Directory.Delete(_testPath, true);
        }
    }

    [Fact]
    public void SanitizeFileName_WithValidName_ReturnsUnchanged()
    {
        var result = PathHelper.SanitizeFileName("My Song Title");

        Assert.Equal("My Song Title", result);
    }

    [Fact]
    public void SanitizeFileName_WithInvalidChars_ReplacesWithUnderscore()
    {
        var result = PathHelper.SanitizeFileName("Song/With/Invalid");
        Assert.Equal("Song_With_Invalid", result);
    }

    [Fact]
    public void SanitizeFileName_WithNullOrEmpty_ReturnsUnknown()
    {
        var resultNull = PathHelper.SanitizeFileName(null!);
        var resultEmpty = PathHelper.SanitizeFileName("");
        var resultWhitespace = PathHelper.SanitizeFileName("   ");

        Assert.Equal("Unknown", resultNull);
        Assert.Equal("Unknown", resultEmpty);
        Assert.Equal("Unknown", resultWhitespace);
    }

    [Fact]
    public void SanitizeFileName_WithLongName_TruncatesTo100Chars()
    {
        var longName = new string('A', 150);

        var result = PathHelper.SanitizeFileName(longName);

        Assert.Equal(100, result.Length);
    }

    [Fact]
    public void SanitizeFolderName_WithValidName_ReturnsUnchanged()
    {
        var result = PathHelper.SanitizeFolderName("Artist Name");

        Assert.Equal("Artist Name", result);
    }

    [Fact]
    public void SanitizeFolderName_WithNullOrEmpty_ReturnsUnknown()
    {
        var resultNull = PathHelper.SanitizeFolderName(null!);
        var resultEmpty = PathHelper.SanitizeFolderName("");
        var resultWhitespace = PathHelper.SanitizeFolderName("   ");

        Assert.Equal("Unknown", resultNull);
        Assert.Equal("Unknown", resultEmpty);
        Assert.Equal("Unknown", resultWhitespace);
    }

    [Fact]
    public void SanitizeFolderName_WithTrailingDots_RemovesDots()
    {
        var result = PathHelper.SanitizeFolderName("Artist Name...");

        Assert.Equal("Artist Name", result);
    }

    [Fact]
    public void SanitizeFolderName_WithInvalidChars_ReplacesWithUnderscore()
    {
        var result = PathHelper.SanitizeFolderName("Artist/With\\Invalid");

        Assert.Equal("Artist_With_Invalid", result);
    }

    [Fact]
    public void BuildTrackPath_WithAllParameters_CreatesCorrectStructure()
    {
        var downloadPath = "/downloads";
        var artist = "Test Artist";
        var album = "Test Album";
        var title = "Test Song";
        var trackNumber = 5;
        var extension = ".mp3";

        var result = PathHelper.BuildTrackPath(downloadPath, artist, album, title, trackNumber, extension);

        Assert.Contains("Test Artist", result);
        Assert.Contains("Test Album", result);
        Assert.Contains("05 - Test Song.mp3", result);
    }

    [Fact]
    public void BuildTrackPath_WithoutTrackNumber_OmitsTrackPrefix()
    {
        var downloadPath = "/downloads";
        var artist = "Test Artist";
        var album = "Test Album";
        var title = "Test Song";
        var extension = ".mp3";

        var result = PathHelper.BuildTrackPath(downloadPath, artist, album, title, null, extension);

        Assert.Contains("Test Song.mp3", result);
        Assert.DoesNotContain(" - Test Song", result.Split(Path.DirectorySeparatorChar).Last());
    }

    [Fact]
    public void BuildTrackPath_WithSingleDigitTrack_PadsWithZero()
    {
        var result = PathHelper.BuildTrackPath("/downloads", "Artist", "Album", "Song", 3, ".mp3");

        Assert.Contains("03 - Song.mp3", result);
    }

    [Fact]
    public void BuildTrackPath_WithFlacExtension_UsesFlacExtension()
    {
        var result = PathHelper.BuildTrackPath("/downloads", "Artist", "Album", "Song", 1, ".flac");

        Assert.EndsWith(".flac", result);
    }

    [Fact]
    public void BuildTrackPath_CreatesArtistAlbumHierarchy()
    {
        var result = PathHelper.BuildTrackPath("/downloads", "My Artist", "My Album", "My Song", 1, ".mp3");
        var parts = result.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Assert.Contains("My Artist", parts);
        Assert.Contains("My Album", parts);

        var artistIndex = Array.IndexOf(parts, "My Artist");
        var albumIndex = Array.IndexOf(parts, "My Album");
        Assert.True(artistIndex < albumIndex, "Artist folder should be parent of Album folder");
    }

    [Fact]
    public void ResolveUniquePath_WhenFileDoesNotExist_ReturnsSamePath()
    {
        var path = Path.Combine(_testPath, "nonexistent.mp3");

        var result = PathHelper.ResolveUniquePath(path);

        Assert.Equal(path, result);
    }

    [Fact]
    public void ResolveUniquePath_WhenFileExists_ReturnsPathWithCounter()
    {
        var basePath = Path.Combine(_testPath, "existing.mp3");
        File.WriteAllText(basePath, "content");

        var result = PathHelper.ResolveUniquePath(basePath);

        Assert.NotEqual(basePath, result);
        Assert.Contains("existing (1).mp3", result);
    }

    [Fact]
    public void ResolveUniquePath_WhenMultipleFilesExist_IncrementsCounter()
    {
        var basePath = Path.Combine(_testPath, "song.mp3");
        var path1 = Path.Combine(_testPath, "song (1).mp3");
        File.WriteAllText(basePath, "content");
        File.WriteAllText(path1, "content");

        var result = PathHelper.ResolveUniquePath(basePath);

        Assert.Contains("song (2).mp3", result);
    }

}
