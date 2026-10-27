using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using allstarr.Core.Health;
using allstarr.Models.Settings;
using Microsoft.Extensions.Options;

namespace allstarr.Tests;

public sealed class HealthSmokeServerProbeTests
{
    [Fact]
    public async Task ServerModeUsesOnlyTheConfiguredApiKeyAndNativeReadRequests()
    {
        using var upstream = new Fixture();
        using var client = new HttpClient(upstream) { BaseAddress = new("http://jellyfin-fixture/") };
        var settings = Options.Create(new JellyfinSettings
        {
            Url = "http://jellyfin-fixture",
            ApiKey = "fixture-api-key",
            ServiceCredentialKind = "api-key"
        });

        var result = await new HealthSmokeServerProbe(client, settings, TimeProvider.System)
            .RunAsync("Fixture song", 15);

        Assert.Contains(result, step => step.Status == "passed");
        Assert.DoesNotContain(result, step => step.Status == "failed");
        Assert.NotEmpty(result);
        Assert.NotEmpty(upstream.Requests);
        Assert.All(upstream.Requests, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Contains(request.Headers.TryGetValues("Authorization", out var authorization)
                ? authorization
                : request.Headers.TryGetValues("X-Emby-Token", out var token) ? token : [],
                value => value.Contains("fixture-api-key", StringComparison.Ordinal));
            Assert.DoesNotContain("userId=", request.RequestUri?.Query ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        });
        Assert.DoesNotContain(upstream.Requests, request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            return path.Contains("AuthenticateByName", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/Users", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/Sessions", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/Playlists", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/Favorites", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/Reports", StringComparison.OrdinalIgnoreCase);
        });
        Assert.Contains(upstream.AudioRanges, range => range.StartsWith("bytes=0-", StringComparison.Ordinal));
        Assert.Contains(upstream.AudioRanges, range => !range.StartsWith("bytes=0-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, "api-key")]
    [InlineData("fixture-user-token", "user-token")]
    public async Task MissingOrUserTokenServiceCredentialDoesNotMakeHttpRequests(string? apiKey, string? kind)
    {
        using var upstream = new Fixture();
        using var client = new HttpClient(upstream) { BaseAddress = new("http://jellyfin-fixture/") };
        var settings = Options.Create(new JellyfinSettings
        {
            Url = "http://jellyfin-fixture",
            ApiKey = apiKey,
            ServiceCredentialKind = kind
        });

        var result = await new HealthSmokeServerProbe(client, settings, TimeProvider.System)
            .RunAsync("Fixture song", 15);

        Assert.Empty(upstream.Requests);
        Assert.Contains(result, step => step.Status == "failed" && step.Reason == "service_api_key_unavailable");
    }

    [Fact]
    public async Task ServerModeUsesUserIndependentMetadataAndOriginalFileRoutes()
    {
        using var upstream = new Fixture { ApiKeyEndpointsOnly = true };
        var results = await RunAsync(upstream);
        Assert.DoesNotContain(results, step => step.Status == "failed");
        Assert.Contains(upstream.Requests, request => request.RequestUri!.AbsolutePath.EndsWith("/Items", StringComparison.Ordinal) &&
            request.RequestUri.Query.Contains("Ids=native-audio", StringComparison.Ordinal));
        Assert.Contains(upstream.Requests, request => request.RequestUri!.AbsolutePath.EndsWith("/File", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingNativeAudioIsNeutralAndDoesNotInventMediaChecks()
    {
        using var upstream = new Fixture { MissingNativeAudio = true };
        var result = await RunAsync(upstream);

        Assert.DoesNotContain(result, step => step.Status == "failed");
        Assert.Equal("passed", Assert.Single(result, step => step.Name == "backend").Status);
        Assert.All(result.Where(step => step.Name != "backend"), step => Assert.Equal("skipped", step.Status));
        Assert.Contains(result, step => step.Name == "search" && step.Reason == "no_native_audio");
        Assert.DoesNotContain(upstream.Requests, request => request.RequestUri?.AbsolutePath.Contains("/Audio/", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Theory]
    [InlineData(true, false, "invalid_range")]
    [InlineData(false, true, "short_body")]
    public async Task InvalidOrShortFarRangeIsFailed(bool malformed, bool shortBody, string reason)
    {
        using var upstream = new Fixture { MalformedRange = malformed, ShortRangeBody = shortBody };
        var result = await RunAsync(upstream);

        Assert.Contains(result, step => step.Name == "seek" && step.Status == "failed" && step.Reason == reason);
    }

    [Fact]
    public async Task OversizedJsonFailsWithoutReturningTheUpstreamBody()
    {
        using var upstream = new Fixture { OversizedJson = true };
        var result = await RunAsync(upstream);
        var serialized = JsonSerializer.Serialize(result);

        Assert.Contains(result, step => step.Name == "backend" && step.Status == "failed" && step.Reason == "response_too_large");
        Assert.DoesNotContain("fixture-private-upstream-body", serialized);
    }

    [Fact]
    public async Task MissingOptionalArtworkAndLyricsRemainNeutral()
    {
        using var upstream = new Fixture { MissingArtwork = true, MissingLyrics = true };
        var result = await RunAsync(upstream);

        Assert.DoesNotContain(result, step => step.Status == "failed");
        Assert.Contains(result, step => step.Name == "artwork" && step.Status == "skipped");
        Assert.Contains(result, step => step.Name == "lyrics" && step.Status == "skipped");
    }

    [Fact]
    public async Task FullHttp200AudioCanBeSampledBeyondTheBoundedPrefix()
    {
        using var upstream = new Fixture { ReturnFullHttp200Audio = true };
        var result = await RunAsync(upstream);

        Assert.Contains(result, step => step.Name == "playback" && step.Status == "passed");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task FarRangeRejectsWrongEndOrInconsistentTotal(bool wrongEnd, bool inconsistentTotal)
    {
        using var upstream = new Fixture { WrongRangeEnd = wrongEnd, InconsistentRangeLength = inconsistentTotal };
        var result = await RunAsync(upstream);

        Assert.Contains(result, step => step.Name == "seek" && step.Status == "failed" && step.Reason == "invalid_range");
    }

    [Fact]
    public async Task PlaybackLatencyMeasuresFirstByteBeforeTheBoundedSampleCompletes()
    {
        using var upstream = new Fixture { SlowAfterFirstByte = true };
        var result = await RunAsync(upstream, firstByteBudgetSeconds: 1);
        var playback = Assert.Single(result, step => step.Name == "playback");

        Assert.Equal("passed", playback.Status);
        Assert.InRange(playback.LatencyMilliseconds, 0, 1000);
    }

    [Fact]
    public async Task LateResponseIsDisposedAfterCallerCancellation()
    {
        using var upstream = new Fixture { IgnoreCancellation = true };
        using var client = new HttpClient(upstream) { BaseAddress = new("http://jellyfin-fixture/") };
        var settings = Options.Create(new JellyfinSettings
        {
            Url = "http://jellyfin-fixture",
            ApiKey = "fixture-api-key",
            ServiceCredentialKind = "api-key"
        });
        using var cancellation = new CancellationTokenSource();
        var run = new HealthSmokeServerProbe(client, settings, TimeProvider.System)
            .RunAsync("Fixture song", 15, cancellation.Token);
        await upstream.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        upstream.Release.TrySetResult(true);
        await upstream.LateResponseReady.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(upstream.LateContent);
        await upstream.LateContent!.DisposedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(upstream.LateContent.Disposed);
    }

    private static async Task<IReadOnlyList<HealthSmokeStep>> RunAsync(Fixture upstream, int firstByteBudgetSeconds = 15)
    {
        using var client = new HttpClient(upstream) { BaseAddress = new("http://jellyfin-fixture/") };
        var settings = Options.Create(new JellyfinSettings
        {
            Url = "http://jellyfin-fixture",
            ApiKey = "fixture-api-key",
            ServiceCredentialKind = "api-key"
        });
        return await new HealthSmokeServerProbe(client, settings, TimeProvider.System)
            .RunAsync("Fixture song", firstByteBudgetSeconds);
    }

    private sealed class Fixture : HttpMessageHandler
    {
        private static readonly byte[] Audio = Enumerable.Range(0, 131_072).Select(index => (byte)(index % 251)).ToArray();
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> AudioRanges { get; } = [];
        public bool MissingNativeAudio { get; init; }
        public bool MalformedRange { get; init; }
        public bool ShortRangeBody { get; init; }
        public bool OversizedJson { get; init; }
        public bool MissingArtwork { get; init; }
        public bool MissingLyrics { get; init; }
        public bool ReturnFullHttp200Audio { get; init; }
        public bool WrongRangeEnd { get; init; }
        public bool InconsistentRangeLength { get; init; }
        public bool SlowAfterFirstByte { get; init; }
        public bool IgnoreCancellation { get; init; }
        public bool ApiKeyEndpointsOnly { get; init; }
        public TaskCompletionSource<bool> RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> LateResponseReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TrackingContent? LateContent { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(request);
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (ApiKeyEndpointsOnly &&
                (path.StartsWith("/Items/", StringComparison.Ordinal) && !path.EndsWith("/File", StringComparison.Ordinal) &&
                    !path.Contains("/Images/", StringComparison.Ordinal) || path.EndsWith("/stream", StringComparison.Ordinal)))
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            if (IgnoreCancellation && path.Contains("/System/Info", StringComparison.OrdinalIgnoreCase))
            {
                RequestStarted.TrySetResult(true);
                await Release.Task;
                LateContent = new TrackingContent(Encoding.UTF8.GetBytes("{\"Id\":\"fixture-server\"}"));
                var late = new HttpResponseMessage(HttpStatusCode.OK) { Content = LateContent };
                LateResponseReady.TrySetResult(true);
                return late;
            }
            if (path.Contains("/Lyrics", StringComparison.OrdinalIgnoreCase))
            {
                if (MissingLyrics) return new HttpResponseMessage(HttpStatusCode.NotFound);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"Lyrics\":[{\"Text\":\"fixture lyrics\"}]}", Encoding.UTF8, "application/json")
                };
            }
            if (path.Contains("/Audio/", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/File", StringComparison.Ordinal))
            {
                var range = request.Headers.Range?.Ranges.SingleOrDefault();
                var start = range?.From ?? 0;
                var end = Math.Min(range?.To ?? (long)Audio.Length - 1, Audio.Length - 1);
                if (start < 0 || start >= Audio.Length || end < start)
                    return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
                AudioRanges.Add(request.Headers.Range?.ToString() ?? string.Empty);
                if (ReturnFullHttp200Audio)
                {
                    var full = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Audio) };
                    full.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
                    return full;
                }
                var bytes = SlowAfterFirstByte && AudioRanges.Count == 1
                    ? null
                    : ShortRangeBody && AudioRanges.Count > 1
                    ? Audio[(int)start..((int)start + 1)]
                    : Audio[(int)start..((int)end + 1)];
                var audio = new HttpResponseMessage(request.Headers.Range == null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
                {
                    Content = bytes == null ? new DelayedAudioContent(Audio[..(int)Math.Min(end - start + 1, 65_536)]) : new ByteArrayContent(bytes)
                };
                audio.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
                if (request.Headers.Range != null)
                    audio.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                        MalformedRange && AudioRanges.Count > 1 ? start + 1 : start,
                        WrongRangeEnd && AudioRanges.Count > 1 ? end - 1 : end,
                        InconsistentRangeLength && AudioRanges.Count > 1 ? Audio.Length + 1 : Audio.Length);
                return audio;
            }

            if (path.Contains("/Images/", StringComparison.OrdinalIgnoreCase) && MissingArtwork)
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            var body = path.Contains("/System/Info", StringComparison.OrdinalIgnoreCase)
                ? OversizedJson ? "{\"Id\":\"fixture-server\",\"body\":\"fixture-private-upstream-body" + new string('x', 2 * 1024 * 1024) + "\"}" : "{\"Id\":\"fixture-server\"}"
                : path.EndsWith("/Items", StringComparison.OrdinalIgnoreCase)
                    ? MissingNativeAudio ? "{\"Items\":[]}" : MissingArtwork
                        ? "{\"Items\":[{\"Id\":\"native-audio\",\"Type\":\"Audio\",\"Path\":\"fixture.mp3\"}]}"
                        : "{\"Items\":[{\"Id\":\"native-audio\",\"Type\":\"Audio\",\"Path\":\"fixture.mp3\",\"ImageTags\":{\"Primary\":\"fixture\"}}]}"
                    : path.Contains("/Images/", StringComparison.OrdinalIgnoreCase)
                        ? "fixture-image"
                        : "{\"Id\":\"native-audio\",\"Type\":\"Audio\",\"Path\":\"fixture.mp3\",\"ImageTags\":{\"Primary\":\"fixture\"}}";
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = path.Contains("/Images/", StringComparison.OrdinalIgnoreCase)
                    ? new ByteArrayContent(Encoding.UTF8.GetBytes(body))
                    : new StringContent(body, Encoding.UTF8, "application/json")
            };
            if (path.Contains("/Images/", StringComparison.OrdinalIgnoreCase))
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return response;
        }

        private sealed class DelayedAudioContent(byte[] bytes) : HttpContent
        {
            protected override Task<Stream> CreateContentReadStreamAsync() =>
                Task.FromResult<Stream>(new DelayedAudioStream(bytes));

            protected override bool TryComputeLength(out long length)
            {
                length = bytes.Length;
                return true;
            }

            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
                await stream.WriteAsync(bytes);
        }

        private sealed class DelayedAudioStream(byte[] bytes) : MemoryStream(bytes)
        {
            private bool _firstRead = true;

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_firstRead)
                {
                    _firstRead = false;
                    return await base.ReadAsync(buffer[..1], cancellationToken);
                }
                await Task.Delay(TimeSpan.FromMilliseconds(1100), cancellationToken);
                return await base.ReadAsync(buffer, cancellationToken);
            }
        }

        public sealed class TrackingContent(byte[] bytes) : ByteArrayContent(bytes)
        {
            public bool Disposed { get; private set; }
            public TaskCompletionSource<bool> DisposedSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            protected override void Dispose(bool disposing)
            {
                Disposed = true;
                base.Dispose(disposing);
                DisposedSignal.TrySetResult(true);
            }
        }
    }
}
