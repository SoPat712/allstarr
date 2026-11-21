using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Capabilities;
using allstarr.Core.ClientApiV2;
using allstarr.Core.Playback;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Middleware;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("lossless", "flac", false)]
    [InlineData("high", "aac", true)]
    [InlineData("normal", "aac", true)]
    [InlineData("low", "aac", true)]
    [InlineData("original", "flac", false)]
    public async Task ClientApiV2_PlaybackResolvesLosslessLibraryTiers(string tier, string codec, bool converted)
    {
        using var factory = V2PlaybackFactory();
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var first = await ResolveV2Playback(client, V2Ids.TrackSource("native1"), tier);
        var second = await ResolveV2Playback(client, "native1", tier);
        Assert.Equal(codec, first["delivered"]!["codec"]!.GetValue<string>());
        Assert.Equal(converted, first["transcoded"]!.GetValue<bool>());
        Assert.Equal(first["cacheKey"]!.GetValue<string>(), second["cacheKey"]!.GetValue<string>());
        Assert.NotEqual(first["playbackId"]!.GetValue<string>(), second["playbackId"]!.GetValue<string>());
        Assert.InRange(DateTimeOffset.Parse(first["expiresAt"]!.GetValue<string>()) - DateTimeOffset.UtcNow,
            TimeSpan.FromHours(11.9), TimeSpan.FromHours(12.1));
        Assert.Equal(converted ? "none" : "bytes", first["seek"]!.GetValue<string>());
        Assert.Equal(converted ? null : (long?)10, first["contentLength"]?.GetValue<long>());
    }

    [Theory]
    [InlineData("lossless", "aac", false)]
    [InlineData("high", "aac", false)]
    [InlineData("normal", "aac", false)]
    [InlineData("low", "aac", true)]
    [InlineData("original", "aac", false)]
    public async Task ClientApiV2_PlaybackResolvesLossyLibraryTiers(string tier, string codec, bool converted)
    {
        using var factory = V2PlaybackFactory(lossy: true);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var body = await ResolveV2Playback(client, "native1", tier);
        Assert.Equal(codec, body["delivered"]!["codec"]!.GetValue<string>());
        Assert.Equal(converted, body["transcoded"]!.GetValue<bool>());
        if (tier == "lossless") Assert.Contains("lossless", body["routeReason"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("lossless", "flac", null)]
    [InlineData("high", "aac", 256000)]
    [InlineData("normal", "aac", 160000)]
    [InlineData("low", "aac", 96000)]
    [InlineData("original", "flac", null)]
    public async Task ClientApiV2_PlaybackResolvesServiceTiersAndPreparesUpcoming(string tier, string codec, int? bitrate)
    {
        var preparation = new Mock<IUpcomingTrackPreparation>();
        using var factory = V2PlaybackFactory(configure: services =>
        {
            services.RemoveAll<IUpcomingTrackPreparation>();
            services.AddSingleton(preparation.Object);
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        await SeedV2DetailsAsync(factory, "listener", "known", personal: true);
        var body = await ResolveV2Playback(client, "ext-deezer-song-known", tier, purpose: "prefetch", diagnostics: factory);
        Assert.Equal(codec, body["delivered"]!["codec"]!.GetValue<string>());
        if (bitrate != null) Assert.Equal(bitrate, body["delivered"]!["bitrate"]!.GetValue<int>());
        preparation.Verify(item => item.PrepareAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task ClientApiV2_PrefetchLooksUpTheUpcomingSongsLyricsToo()
    {
        var asked = new TaskCompletionSource<ProtocolExecutionContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lyrics = new Mock<IProtocolLyricsResolver>();
        lyrics.Setup(item => item.GetRichLyricsAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<allstarr.Models.Domain.Song>(),
                It.IsAny<string>(), It.IsAny<ProtocolLyricsSources>()))
            .Callback((ProtocolExecutionContext context, allstarr.Models.Domain.Song _, string _, ProtocolLyricsSources? _) => asked.TrySetResult(context))
            .ReturnsAsync((RichLyrics?)null);
        using var factory = V2PlaybackFactory(configure: services =>
        {
            services.RemoveAll<IProtocolLyricsResolver>();
            services.AddSingleton(lyrics.Object);
            services.AddSingleton<IBackendLibraryAccessResolver>(provider =>
                new SignedInAccess(ActivatorUtilities.CreateInstance<BackendLibraryAccessResolver>(provider), provider));
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        await SeedV2DetailsAsync(factory, "listener", "known", personal: true);
        Mock.Get(factory.Services.GetRequiredService<IProtocolProviderGateway>())
            .Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "known"))
            .ReturnsAsync(new allstarr.Models.Domain.Song { Id = "ext-deezer-song-known", Title = "Stored song", Artist = "Artist", ExternalProvider = "deezer", ExternalId = "known" });
        await ResolveV2Playback(client, "ext-deezer-song-known", "lossless", purpose: "prefetch", diagnostics: factory);

        var context = await asked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(context.IsPreparation);
        Assert.Equal("ext-deezer-song-known", context.RequestedItemId);
    }

    [Theory]
    [InlineData("ext-deezer-song-known", "High", null, "household", 256000)]
    [InlineData("ext-deezer-song-known", "BestAvailable", "DataSaver", "listener", 96000)]
    [InlineData("ext-deezer-song-known", "CdLossless", null, "household", null)]
    [InlineData("native1", "BestAvailable", "DataSaver", "listener", 96000)]
    [InlineData("native1", "High", "DataSaver", "listener", 96000)]
    public async Task ClientApiV2_PlaybackAppliesQualityCeilings(string song, string household, string? personal, string loweredBy, int? bitrate)
    {
        using var factory = V2PlaybackFactory(configure: services =>
        {
            var policies = new Mock<IEffectiveProviderPolicyResolver>();
            var snapshot = new EffectiveProviderPolicySnapshot(ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
                ImmutableHashSet<string>.Empty, AudioQualityPolicy.Lower(household, personal), 0)
            { Preferences = new(QualityLimit: personal) };
            policies.Setup(item => item.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(snapshot with { AudioQuality = household });
            policies.Setup(item => item.ResolveForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid user, CancellationToken _) => snapshot with { UserId = user });
            services.RemoveAll<IEffectiveProviderPolicyResolver>();
            services.AddSingleton(policies.Object);
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        if (song.StartsWith("ext-", StringComparison.Ordinal)) await SeedV2DetailsAsync(factory, "listener", "known", personal: true);
        var body = await ResolveV2Playback(client, song, "lossless", diagnostics: factory);
        Assert.Equal(loweredBy, body["loweredBy"]!.GetValue<string>());
        if (bitrate != null) Assert.Equal(bitrate, body["delivered"]!["bitrate"]!.GetValue<int>());
        else
        {
            Assert.Equal(44100, body["delivered"]!["sampleRate"]!.GetValue<int>());
            Assert.Equal(16, body["delivered"]!["bitDepth"]!.GetValue<int>());
        }
    }

    [Theory]
    [InlineData("DataSaver")]
    [InlineData("High")]
    [InlineData("CdLossless")]
    public async Task ClientApiV2_TheHouseholdLimitLeavesTheListenersOwnFilesAsTheyAre(string household)
    {
        using var factory = V2PlaybackFactory(configure: services =>
        {
            var policies = new Mock<IEffectiveProviderPolicyResolver>();
            var snapshot = new EffectiveProviderPolicySnapshot(ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
                ImmutableHashSet<string>.Empty, household, 0);
            policies.Setup(item => item.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(snapshot);
            policies.Setup(item => item.ResolveForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid user, CancellationToken _) => snapshot with { UserId = user });
            services.RemoveAll<IEffectiveProviderPolicyResolver>();
            services.AddSingleton(policies.Object);
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");

        // The file as it is: not lowered, not converted, and still seekable by bytes.
        var original = await ResolveV2Playback(client, "native1", "original");
        Assert.Null(original["loweredBy"]);
        Assert.False(original["transcoded"]!.GetValue<bool>());
        Assert.Equal("bytes", original["seek"]!.GetValue<string>());
        Assert.Equal(original["source"]!.ToJsonString(), original["delivered"]!.ToJsonString());

        // What the app itself asks for is still obeyed.
        var low = await ResolveV2Playback(client, "native1", "low");
        Assert.Null(low["loweredBy"]);
        Assert.Equal(96000, low["delivered"]!["bitrate"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(null, "aac")]
    [InlineData("aac,mp3", "aac")]
    [InlineData("mp3", "mp3")]
    public async Task ClientApiV2_PlaybackUsesMp3OnlyWithoutAac(string? codecs, string expected)
    {
        using var factory = V2PlaybackFactory();
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var body = await ResolveV2Playback(client, "native1", "high", codecs);
        Assert.Equal(expected, body["delivered"]!["codec"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("alac", "alac")]
    [InlineData("aac", "aac")]
    public async Task ClientApiV2_PlaybackLosslessRespectsTheClientsCodecs(string codecs, string expected)
    {
        using var factory = V2PlaybackFactory();
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var body = await ResolveV2Playback(client, "native1", "lossless", codecs);
        Assert.Equal(expected, body["delivered"]!["codec"]!.GetValue<string>());
        Assert.True(body["transcoded"]!.GetValue<bool>());
        if (expected == "aac") Assert.Contains("client", body["routeReason"]!.GetValue<string>());
    }

    [Fact]
    public async Task ClientApiV2_PlaybackStreamAcceptsBareRangesAndHeadAndRejectsInvalidTokens()
    {
        using var factory = V2PlaybackFactory();
        using var client = CreateClientApiClient(factory);
        using var unauthenticated = await client.PostAsJsonAsync("/allstarr/v2/playback/resolve", new { trackId = "native1", tier = "original" });
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var body = await ResolveV2Playback(client, "native1", "original");
        var url = body["url"]!.GetValue<string>();
        client.DefaultRequestHeaders.Authorization = null;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new(2, 5);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(new byte[] { 2, 3, 4, 5 }, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(body["playbackId"]!.GetValue<string>(), response.Headers.GetValues("X-Allstarr-Playback-Request").Single());
        using var head = await client.SendAsync(new(HttpMethod.Head, url));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        using var tampered = await client.GetAsync(url + "x");
        Assert.Equal(HttpStatusCode.NotFound, tampered.StatusCode);
        var protector = factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(V2PlaybackTokens.Purpose).ToTimeLimitedDataProtector();
        var plain = protector.Unprotect(url[(url.LastIndexOf('/') + 1)..], out _);
        var expired = protector.Protect(plain, DateTimeOffset.UtcNow.AddHours(-1));
        using var expiredResponse = await client.GetAsync("/allstarr/v2/stream/" + expired);
        Assert.Equal(HttpStatusCode.NotFound, expiredResponse.StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "other-token");
        using var foreign = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    [Fact]
    public async Task ClientApiV2_ResolveStaysOutsideBatch()
    {
        using var factory = V2PlaybackFactory();
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        using var response = await client.PostAsJsonAsync("/allstarr/v2/batch", new
        {
            requests = new[] { new { id = "closed", path = "/allstarr/v2/playback/resolve" } }
        });
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal(404, body["responses"]!["closed"]!["status"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("bogus", "native1", HttpStatusCode.BadRequest)]
    [InlineData("high", "unknown", HttpStatusCode.NotFound)]
    [InlineData("high", "trk_s!!", HttpStatusCode.BadRequest)]
    public async Task ClientApiV2_PlaybackRejectsInvalidTiersAndInaccessibleTracks(string tier, string trackId, HttpStatusCode expected)
    {
        using var factory = V2PlaybackFactory();
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        using var response = await client.PostAsJsonAsync("/allstarr/v2/playback/resolve", new { trackId, tier });
        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task ClientApiV2_PlaybackTokenFromAnotherKeyRingIsNotFound()
    {
        using var factory = V2PlaybackFactory();
        using var client = CreateClientApiClient(factory);
        var foreign = new EphemeralDataProtectionProvider();
        var token = foreign.CreateProtector(V2PlaybackTokens.Purpose).ToTimeLimitedDataProtector().Protect("{}", TimeSpan.FromHours(12));
        using var response = await client.GetAsync("/allstarr/v2/stream/" + token);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ClientApiV2_PlaybackStoredServiceStreamSupportsBareRanges()
    {
        using var factory = V2PlaybackFactory();
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        await SeedV2DetailsAsync(factory, "listener", "known", personal: true);
        var body = await ResolveV2Playback(client, "ext-deezer-song-known", "original", diagnostics: factory);
        client.DefaultRequestHeaders.Authorization = null;
        using var request = new HttpRequestMessage(HttpMethod.Get, body["url"]!.GetValue<string>());
        request.Headers.Range = new(2, 5);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(new byte[] { 2, 3, 4, 5 }, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(body["playbackId"]!.GetValue<string>(), response.Headers.GetValues("X-Allstarr-Playback-Request").Single());
        var timings = await factory.Services.GetRequiredService<PlaybackStartTimingStore>().ReadAsync(10, CancellationToken.None);
        Assert.Contains(timings, timing => timing.Id == Guid.Parse(body["playbackId"]!.GetValue<string>()));
    }

    [Fact]
    public async Task ClientApiV2_PlaybackKeepsTheLinkUsableWhenSourceValidatorsChange()
    {
        var changed = false;
        using var factory = V2PlaybackFactory(audio: request =>
        {
            var response = V2AudioResponse(request);
            if (changed) response.Headers.ETag = new("\"changed-source\"");
            return response;
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var body = await ResolveV2Playback(client, "native1", "original");
        changed = true;
        var updated = await ResolveV2Playback(client, "native1", "original");
        Assert.NotEqual(body["cacheKey"]!.GetValue<string>(), updated["cacheKey"]!.GetValue<string>());
        client.DefaultRequestHeaders.Authorization = null;
        using var response = await client.GetAsync(body["url"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ClientApiV2_PlaybackPrefetchCanMoveTheSourceIntoCache()
    {
        var cached = false;
        var preparation = new Mock<IUpcomingTrackPreparation>();
        preparation.Setup(item => item.PrepareAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>()))
            .Returns(() => { cached = true; return Task.CompletedTask; });
        using var factory = V2PlaybackFactory(configure: services =>
        {
            services.RemoveAll<IUpcomingTrackPreparation>();
            services.AddSingleton(preparation.Object);
        }, source: () =>
        {
            var stream = V2ServiceAudio(cached);
            if (cached) stream.Response.Headers.ETag = null;
            return stream;
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        await SeedV2DetailsAsync(factory, "listener", "known", personal: true);
        var first = await ResolveV2Playback(client, "ext-deezer-song-known", "original", purpose: "prefetch", diagnostics: factory);
        var second = await ResolveV2Playback(client, "ext-deezer-song-known", "original");
        Assert.True(cached);
        Assert.Equal(first["cacheKey"]!.GetValue<string>(), second["cacheKey"]!.GetValue<string>());
        client.DefaultRequestHeaders.Authorization = null;
        using var request = new HttpRequestMessage(HttpMethod.Get, first["url"]!.GetValue<string>());
        request.Headers.Range = new(2, 5);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(new byte[] { 2, 3, 4, 5 }, await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("low", 96000)]
    [InlineData("lowest", 64000)]
    public async Task ClientApiV2_AConvertedDownloadIsAFileThatCanResume(string tier, int bitrate)
    {
        var asked = new List<string>();
        var copy = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
        using var factory = V2PlaybackFactory(converted: request =>
        {
            lock (asked) asked.Add(request.RequestUri!.Host + ":" + request.RequestUri.Port);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(copy) };
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");

        // Playing the same tier stays a live conversion; only a download is promised a file.
        Assert.Equal("none", (await ResolveV2Playback(client, "native1", tier))["seek"]!.GetValue<string>());
        var body = await ResolveV2Playback(client, "native1", tier, purpose: "download");
        Assert.True(body["transcoded"]!.GetValue<bool>());
        Assert.Equal(bitrate, body["delivered"]!["bitrate"]!.GetValue<int>());
        Assert.Equal("bytes", body["seek"]!.GetValue<string>());
        Assert.Null(body["contentLength"]);
        var url = body["url"]!.GetValue<string>();
        client.DefaultRequestHeaders.Authorization = null;

        // Before the copy exists a HEAD is answered as any conversion is, and makes nothing.
        using (var head = await client.SendAsync(new(HttpMethod.Head, url)))
        {
            Assert.Equal(HttpStatusCode.OK, head.StatusCode);
            Assert.Null(head.Content.Headers.ContentLength);
        }
        Assert.Empty(asked);

        using var whole = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, whole.StatusCode);
        Assert.Equal(copy, await whole.Content.ReadAsByteArrayAsync());
        Assert.Equal(copy.Length, whole.Content.Headers.ContentLength);
        Assert.Equal("audio/aac", whole.Content.Headers.ContentType?.MediaType);
        Assert.Contains("bytes", whole.Headers.AcceptRanges);
        var tag = whole.Headers.ETag;
        Assert.NotNull(tag);
        Assert.Equal(["127.0.0.1:8080"], asked);

        // A dropped download picks up where it stopped, from the copy, without converting again.
        using var resume = new HttpRequestMessage(HttpMethod.Get, url);
        resume.Headers.Range = new(60, null);
        resume.Headers.IfRange = new(tag!);
        using var rest = await client.SendAsync(resume);
        Assert.Equal(HttpStatusCode.PartialContent, rest.StatusCode);
        Assert.Equal(copy[60..], await rest.Content.ReadAsByteArrayAsync());
        Assert.Equal(new(60, 63, 64), rest.Content.Headers.ContentRange);
        Assert.Single(asked);

        // A new link for the same song and tier names the same copy and now knows its size.
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var again = await ResolveV2Playback(client, "native1", tier, purpose: "download");
        Assert.Equal(body["cacheKey"]!.GetValue<string>(), again["cacheKey"]!.GetValue<string>());
        Assert.Equal(copy.Length, again["contentLength"]!.GetValue<long>());
        client.DefaultRequestHeaders.Authorization = null;
        using var second = await client.GetAsync(again["url"]!.GetValue<string>());
        Assert.Equal(tag, second.Headers.ETag);
        Assert.Single(asked);
    }

    [Fact]
    public async Task ClientApiV2_AConvertedDownloadSaysWhenToAskAgainWhileTheConverterIsBusy()
    {
        using var factory = V2PlaybackFactory(converted: _ =>
        {
            var busy = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            busy.Headers.RetryAfter = new(TimeSpan.FromSeconds(7));
            return busy;
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var body = await ResolveV2Playback(client, "native1", "low", purpose: "download");
        client.DefaultRequestHeaders.Authorization = null;
        using var response = await client.GetAsync(body["url"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(7), response.Headers.RetryAfter?.Delta);
    }

    [Fact]
    public async Task ClientApiV2_ADownloadOfTheFileItselfNeedsNoCopy()
    {
        using var factory = V2PlaybackFactory();
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var body = await ResolveV2Playback(client, "native1", "original", purpose: "download");
        Assert.False(body["transcoded"]!.GetValue<bool>());
        Assert.Equal("bytes", body["seek"]!.GetValue<string>());
        client.DefaultRequestHeaders.Authorization = null;
        using var request = new HttpRequestMessage(HttpMethod.Get, body["url"]!.GetValue<string>());
        request.Headers.Range = new(2, 5);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(new byte[] { 2, 3, 4, 5 }, await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientApiV2_PlaybackLiveTranscodeHeadIgnoresRanges(bool external)
    {
        using var factory = V2PlaybackFactory();
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        if (external) await SeedV2DetailsAsync(factory, "listener", "known", personal: true);
        var body = await ResolveV2Playback(client, external ? "ext-deezer-song-known" : "native1", "high");
        client.DefaultRequestHeaders.Authorization = null;
        using var request = new HttpRequestMessage(HttpMethod.Head, body["url"]!.GetValue<string>());
        request.Headers.Range = new(999999, null);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Content.Headers.ContentRange);
        Assert.Equal("audio/aac", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    private static async Task<JsonNode> ResolveV2Playback(HttpClient client, string trackId, string tier, string? codecs = null,
        string purpose = "play", ProtocolFactory? diagnostics = null)
    {
        using var response = await client.PostAsJsonAsync("/allstarr/v2/playback/resolve", new
        { trackId, tier, purpose, client = codecs == null ? null : new { codecs = codecs.Split(',') } });
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, text + "\n" + diagnostics?.Services.GetRequiredService<V2PlaybackErrors>().Last);
        return JsonNode.Parse(text)!;
    }

    // Background work acts as the signed-in listener, as it does for a listener with a stored media server sign-in.
    private sealed class SignedInAccess(IBackendLibraryAccessResolver inner, IServiceProvider services) : IBackendLibraryAccessResolver
    {
        public Task<BackendLibraryAccess> ResolveAsync(ProtocolExecutionContext context, CancellationToken cancellationToken = default) =>
            inner.ResolveAsync(context, cancellationToken);

        public async Task<BackendLibraryAccessContext> ResolveUserAsync(Guid userId, CancellationToken cancellationToken = default) =>
            new(new ProtocolExecutionContext(ProtocolKind.Jellyfin, "primary", "listener",
                await services.GetRequiredService<allstarr.Core.Identity.BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener"), cancellationToken),
                "fixture", DateTimeOffset.UtcNow.AddMinutes(1), cancellationToken), new(true, []));

        public Task InvalidateAsync(ProtocolKind protocol, string backendInstanceId, string backendPrincipalId) =>
            inner.InvalidateAsync(protocol, backendInstanceId, backendPrincipalId);
    }

    private static ProtocolFactory V2PlaybackFactory(bool lossy = false, Action<IServiceCollection>? configure = null,
        Func<HttpRequestMessage?, HttpResponseMessage>? audio = null, Func<ProtocolProviderStream>? source = null,
        Func<HttpRequestMessage, HttpResponseMessage>? converted = null)
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetPlayableProviderOrderAsync(It.IsAny<ProviderActorContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(["deezer"]);
        gateway.Setup(item => item.GetStoredSongAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new allstarr.Models.Domain.Song { Id = "ext-deezer-song-known", Title = "Stored song", ExternalProvider = "deezer", ExternalId = "known" });
        gateway.Setup(item => item.OpenStreamAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<ProviderAudioQuality>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync(() => source?.Invoke() ?? V2ServiceAudio());
        return ClientDetailsFactory(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users/Me")
                return Json(200, request.Headers.Authorization?.ToString().Contains("other-token") == true ? """{"Id":"other"}""" : """{"Id":"listener"}""");
            if (request.RequestUri.AbsolutePath == "/UserViews") return Json(200, """{"Items":[]}""");
            if (request.RequestUri.AbsolutePath == "/Users/listener/Items")
                return Json(200, lossy ? V2LibrarySong.Replace("flac", "aac").Replace("2000000", "128000") : V2LibrarySong);
            // A download's converted copy is read from the server's own stream route; the fixture stands in for that route.
            if (request.RequestUri.AbsolutePath.StartsWith("/allstarr/v2/stream/", StringComparison.Ordinal))
                return (converted ?? throw new InvalidOperationException("No converted copy was expected to be made."))(request);
            Assert.Equal("/Audio/native1/stream", request.RequestUri.AbsolutePath);
            Assert.DoesNotContain("listener-token", request.Headers.Authorization?.ToString() ?? "");
            return audio?.Invoke(request) ?? V2AudioResponse(request);
        }, services =>
        {
            var policies = new Mock<IEffectiveProviderPolicyResolver>();
            var snapshot = new EffectiveProviderPolicySnapshot(ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
                ImmutableHashSet<string>.Empty, AudioQualityPolicy.DefaultStep, 0);
            policies.Setup(item => item.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(snapshot);
            policies.Setup(item => item.ResolveForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid user, CancellationToken _) => snapshot with { UserId = user });
            services.AddSingleton<V2PlaybackErrors>();
            services.AddSingleton<ILogger<ClientApiMiddleware>>(provider => provider.GetRequiredService<V2PlaybackErrors>());
            services.RemoveAll<IEffectiveProviderPolicyResolver>();
            services.AddSingleton(policies.Object);
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
            configure?.Invoke(services);
        });
    }

    private sealed class V2PlaybackErrors : ILogger<ClientApiMiddleware>
    {
        internal Exception? Last { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (exception != null) Last = exception; }
    }

    private static ProtocolProviderStream V2ServiceAudio(bool cached = true) => new(V2AudioResponse(),
        new("fixture", new("https://fixture.invalid/audio"), DateTimeOffset.UtcNow.AddMinutes(1), true, true,
            new("audio/flac", "flac", "flac", 2000000, 96000, 24, 2), ProviderStreamRetryBehavior.DoNotRetry),
        "deezer", "known", IsCached: cached);

    private static HttpResponseMessage V2AudioResponse(HttpRequestMessage? request = null)
    {
        byte[] bytes = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9];
        var range = request?.Headers.Range?.Ranges.SingleOrDefault();
        var response = new HttpResponseMessage(range == null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
        { Content = new ByteArrayContent(range == null ? bytes : bytes[(int)range.From!.Value..((int)range.To!.Value + 1)]) };
        response.Headers.AcceptRanges.Add("bytes");
        response.Headers.ETag = new("\"stable-source\"");
        response.Content.Headers.ContentType = new("audio/flac");
        if (range != null) response.Content.Headers.ContentRange = new(range.From!.Value, range.To!.Value, bytes.Length);
        return response;
    }
}
