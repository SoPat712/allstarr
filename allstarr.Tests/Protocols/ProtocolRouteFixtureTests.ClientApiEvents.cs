using allstarr.Core.Playback;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using allstarr.Core.ClientEvents;
using allstarr.Core.Identity;
using allstarr.Core.Capabilities;
using allstarr.Core.Favorites;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Playlists;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using System.IO.Pipelines;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task ClientApi_EventsReadyHeartbeatCapsAndDisconnect()
    {
        var buffering = new ClientEventBufferingFilter();
        using var factory = ClientApiFactory(_ => Json(200, """{"Id":"listener"}"""), services =>
        {
            services.AddSingleton<IStartupFilter>(buffering);
            services.RemoveAll<ClientEventOptions>();
            services.AddSingleton(new ClientEventOptions { HeartbeatInterval = TimeSpan.FromMilliseconds(30), ConnectionsPerUser = 1 });
        });
        using var client = CreateClientApiClient(factory);
        using var stream = await OpenClientEventsAsync(client);
        Assert.Equal("text/event-stream", stream.Response.Content.Headers.ContentType!.MediaType);
        Assert.Empty(stream.Response.Content.Headers.ContentEncoding);
        Assert.True(buffering.Disabled);
        Assert.True(stream.Response.Headers.CacheControl!.NoStore);
        Assert.Equal("no", stream.Response.Headers.GetValues("X-Accel-Buffering").Single());
        Assert.Equal(": ping", await stream.FrameAsync());
        using var capped = await client.SendAsync(ClientPlaybackRequest("/allstarr/v1/events"));
        await AssertClientApiError(capped, 503, "unavailable");
        stream.Dispose();
        var hub = factory.Services.GetRequiredService<ClientEventHub>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (hub.ConnectionCount != 0) await Task.Delay(10, timeout.Token);
        using var replacement = await OpenClientEventsAsync(client);
        factory.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>().StopApplication();
        while (hub.ConnectionCount != 0) await Task.Delay(10, timeout.Token);
    }

    [Fact]
    public async Task ClientApi_EventsOffSwitchReturnsNotFoundBeforeAuthentication()
    {
        using var factory = ClientApiFactory(_ => throw new InvalidOperationException("Unexpected upstream request"));
        using var client = CreateClientApiClient(factory, enabled: false);
        using var response = await client.GetAsync("/allstarr/v1/events");
        await AssertClientApiError(response, 404, "not_found");
        Assert.Equal(0, factory.Services.GetRequiredService<ClientEventHub>().ConnectionCount);
    }

    [Fact]
    public async Task ClientApi_EventsLyricsPinAndClearPublishCommittedRevision()
    {
        var recording = Guid.NewGuid();
        using var factory = ClientLyricsFactory(recording, new() { ["lyricsplus"] = ClientLyricsResult("word") });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var (owner, _) = await SeedClientLyricsAsync(factory, recording);
        using var stream = await OpenClientEventsAsync(client);
        using var foreign = factory.Services.GetRequiredService<ClientEventHub>().Subscribe(Guid.NewGuid(), "phone")!;
        const string path = "/allstarr/v1/items/ext-deezer-song-lyrics/lyrics";
        var sources = await ReadClientLyricsJsonAsync(client, path + "/sources");
        using var pin = await client.PutAsJsonAsync(path + "/source", new { sourceId = "lyricsplus", expectedRevision = sources["revision"] });
        Assert.Equal(HttpStatusCode.OK, pin.StatusCode);
        using var body = JsonDocument.Parse(await pin.Content.ReadAsStringAsync());
        var changed = await stream.EventAsync("lyrics.updated", "ext-deezer-song-lyrics");
        Assert.Equal("ext-deezer-song-lyrics", changed.GetProperty("itemId").GetString());
        Assert.Equal(body.RootElement.GetProperty("revision").ToString(), changed.GetProperty("revision").ToString());
        using var clear = await client.PostAsJsonAsync(path + "/source/clear", new { expectedRevision = body.RootElement.GetProperty("revision") });
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await stream.EventAsync("lyrics.updated", "ext-deezer-song-lyrics")).GetProperty("revision").GetProperty("personal").ValueKind);
        Assert.Equal(0, foreign.PendingCount);
        Assert.Contains(owner, factory.Services.GetRequiredService<ClientEventHub>().Listeners);
    }

    [Fact]
    public async Task ClientApi_EventsMatchSetRejectClearPublishOnlyToOwner()
    {
        using var factory = ClientMatchFactory(false);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var (_, other, _) = await SeedClientMatchAsync(factory);
        using var stream = await OpenClientEventsAsync(client);
        using var foreign = factory.Services.GetRequiredService<ClientEventHub>().Subscribe(other, "phone")!;
        const string path = "/allstarr/v1/items/ext-deezer-song-match/match";
        var initial = await ReadClientLyricsJsonAsync(client, path);
        var candidate = initial["candidates"]!.AsArray().Single(item => item!["provider"]!["id"]!.GetValue<string>() == "deezer")!;
        using var set = await client.PutAsJsonAsync(path, new { candidateId = candidate["candidateId"]!.GetValue<string>(), expectedRevision = initial["revision"]!.GetValue<string>() });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        var changed = await stream.EventAsync("match.updated", "ext-deezer-song-match");
        Assert.Equal("ext-deezer-song-match", changed.GetProperty("itemId").GetString());
        var personal = await ReadClientLyricsJsonAsync(client, path);
        Assert.Equal(personal["state"]!.GetValue<string>(), changed.GetProperty("state").GetString());
        using var reject = await client.PostAsJsonAsync(path + "/reject", new { expectedRevision = personal["revision"]!.GetValue<string>() });
        Assert.Equal(HttpStatusCode.OK, reject.StatusCode);
        Assert.Equal("rejected", (await stream.EventAsync("match.updated", "ext-deezer-song-match")).GetProperty("state").GetString());
        var rejected = await ReadClientLyricsJsonAsync(client, path);
        using var clear = await client.DeleteAsync(path + "?expectedRevision=" + rejected["revision"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        Assert.NotEqual("rejected", (await stream.EventAsync("match.updated", "ext-deezer-song-match")).GetProperty("state").GetString());
        Assert.Equal(0, foreign.PendingCount);
    }

    [Fact]
    public async Task ClientApi_EventsHouseholdAndAutomaticMatchesRespectPersonalChoices()
    {
        using var factory = ClientMatchFactory(false);
        using var client = CreateClientApiClient(factory);
        var (owner, other, snapshot) = await SeedClientMatchAsync(factory);
        var commands = factory.Services.GetRequiredService<TrackMatchCommandService>();
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        await using (var context = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            var source = await context.ExternalMetadataSnapshots.SingleAsync();
            context.ExternalMetadataSnapshots.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = other,
                BackendInstanceId = "primary",
                BackendPrincipalId = "other",
                Protocol = "jellyfin",
                ProviderId = source.ProviderId,
                ResourceKind = "track",
                ExternalIdHash = source.ExternalIdHash,
                SnapshotVersion = 1,
                RetrievedAt = DateTimeOffset.UtcNow,
                PayloadJson = source.PayloadJson,
                PayloadSha256 = source.PayloadSha256
            });
            await context.SaveChangesAsync();
        }
        var initial = await ReadClientLyricsJsonAsync(client, "/allstarr/v1/items/ext-deezer-song-match/match");
        using var stream = await OpenClientEventsAsync(client);
        using var foreign = factory.Services.GetRequiredService<ClientEventHub>().Subscribe(other, null)!;
        var household = await commands.ResolveSnapshotAsync(new(owner, true), snapshot,
            new("reject", AuthorityScope: "household", ExpectedAuthority: await HouseholdStamp(),
                ExpectedMatchRevision: initial["revision"]!.GetValue<string>()), "fixture");
        Assert.True(household.Succeeded);
        Assert.Equal("rejected", (await stream.EventAsync("match.updated", "ext-deezer-song-match")).GetProperty("state").GetString());
        var stamp = (await HouseholdStamp())!;
        Assert.True((await commands.ClearManualAuthorityAsync(new(owner, true), snapshot, ManualTrackAuthorityKind.Rejection,
            stamp.Id, stamp.Revision, "fixture", authorityScope: "household")).Succeeded);
        await stream.EventAsync("match.updated", "ext-deezer-song-match");
        var principal = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!;
        var execution = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "primary", "listener", principal,
            "fixture", DateTimeOffset.UtcNow.AddMinutes(1), default);
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        var previous = await db.TrackMatches.SingleAsync();
        await commands.RecordDecisionAsync(execution, new(snapshot, null, previous.CanonicalRecordingId,
            TrackMatchState.Unresolved, 0, .88, 2, 1, 0, "fixture", "fixture", "[]", "[]", "[]"));
        var changed = await stream.EventAsync("match.updated", "ext-deezer-song-match");
        var current = await client.SendAsync(ClientPlaybackRequest("/allstarr/v1/items/ext-deezer-song-match/match"));
        using var body = JsonDocument.Parse(await current.Content.ReadAsStringAsync());
        Assert.Equal(body.RootElement.GetProperty("state").GetString(), changed.GetProperty("state").GetString());
        var firstLocal = Guid.NewGuid();
        var secondLocal = Guid.NewGuid();
        foreach (var id in new[] { firstLocal, secondLocal }) db.LibraryTracks.Add(new()
        {
            Id = id,
            OwnerUserId = owner,
            Protocol = "jellyfin",
            BackendInstanceId = "primary",
            BackendLibraryId = "library",
            BackendItemId = id.ToString("N"),
            CanonicalRecordingId = previous.CanonicalRecordingId,
            Title = "Fixture",
            Artist = "Artist",
            Revision = 1
        });
        await db.SaveChangesAsync();
        await commands.RecordDecisionAsync(execution, new(snapshot, firstLocal, previous.CanonicalRecordingId,
            TrackMatchState.Accepted, .97, .88, 3, 1, 0, "fixture", "fixture", "[]", "[]", "[]"));
        await stream.EventAsync("match.updated", "ext-deezer-song-match");
        await commands.SetOverrideAsync(execution, new(snapshot, ManualOverrideDecision.Reject, null, "Rejected candidate"));
        Assert.Equal("rejected", (await stream.EventAsync("match.updated", "ext-deezer-song-match")).GetProperty("state").GetString());
        await commands.RecordDecisionAsync(execution, new(snapshot, secondLocal, previous.CanonicalRecordingId,
            TrackMatchState.Accepted, .97, .88, 4, 1, 0, "fixture", "fixture", "[]", "[]", "[]"));
        Assert.Equal("accepted", (await stream.EventAsync("match.updated", "ext-deezer-song-match")).GetProperty("state").GetString());
        Assert.Equal(0, foreign.PendingCount);

        async Task<ManualAuthorityRevision?> HouseholdStamp()
        {
            await using var context = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
            var row = await context.ManualTrackOverrides.SingleOrDefaultAsync(item => item.OwnerUserId == null && item.RevokedAt == null);
            return row == null ? null : new(row.Id, row.Revision);
        }
    }

    [Fact]
    public async Task ClientApi_EventsEffectiveLyricsChangesAreObservedThroughBackgroundLookup()
    {
        var recording = Guid.NewGuid();
        var results = new Dictionary<string, ProviderLyricsResult?> { ["lyricsplus"] = ClientLyricsResult("line") };
        using var factory = ClientLyricsFactory(recording, results);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        await SeedClientLyricsAsync(factory, recording);
        const string path = "/allstarr/v1/items/ext-deezer-song-lyrics/lyrics";
        await ReadClientLyricsJsonAsync(client, path);
        using var stream = await OpenClientEventsAsync(client);
        results["lyricsplus"] = new(ProviderLyricsAvailabilityState.Available, "fixture", ProviderLyricsFormat.WordTimed,
            "[00:01.00]New words", timedLines:
            [new("New words", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8),
                [new("New ", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)), new("words", TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5))])]);
        var principal = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!;
        var execution = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "primary", "listener", principal,
            "fixture", DateTimeOffset.UtcNow.AddMinutes(1), default)
        { RequestedItemId = "ext-deezer-song-lyrics" };
        await factory.Services.GetRequiredService<IProtocolLyricsResolver>().FindAsync(execution,
            new allstarr.Models.Domain.Song { CanonicalRecordingId = recording, Title = "Fixture", Artist = "Artist" },
            recording.ToString("D"), "deezer", "lyrics", null);
        var updated = await stream.EventAsync("lyrics.updated", "ext-deezer-song-lyrics");
        Assert.Equal("ext-deezer-song-lyrics", updated.GetProperty("itemId").GetString());
        Assert.Equal(64, updated.GetProperty("revision").GetString()!.Length);
    }

    [Fact]
    public async Task ClientApi_EventsKeepIntentChangesUseItemDetailsStates()
    {
        var recording = Guid.NewGuid();
        using var factory = ClientLyricsFactory(recording, new() { ["lyricsplus"] = ClientLyricsResult("word") });
        using var client = CreateClientApiClient(factory);
        await SeedClientLyricsAsync(factory, recording);
        using var stream = await OpenClientEventsAsync(client);
        var principal = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!;
        var execution = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "primary", "listener", principal,
            "fixture", DateTimeOffset.UtcNow.AddMinutes(1), default);
        var pipeline = factory.Services.GetRequiredService<IFavoriteActionPipeline>();
        await pipeline.RecordAsync(new(execution, "ext-deezer-song-lyrics", FavoriteOperation.Favorite, "first"));
        var keep = await stream.EventAsync("keep.updated");
        Assert.Equal("ext-deezer-song-lyrics", keep.GetProperty("itemId").GetString());
        Assert.Equal("inProgress", keep.GetProperty("state").GetString());
        var queue = factory.Services.GetRequiredService<DurableJobQueue>();
        var claim = await queue.ClaimNextAsync("fixture-events", [FavoriteActionPipeline.JobType]);
        Assert.NotNull(claim);
        Assert.True(await queue.ReportProgressAsync(claim, new("download", "Downloading", Completed: 1, Total: 2)));
        Assert.Equal(.5, (await stream.EventAsync("keep.updated")).GetProperty("progress").GetDouble());
        await pipeline.RecordAsync(new(execution, "ext-deezer-song-lyrics", FavoriteOperation.Unfavorite, "second"));
        Assert.Equal("none", (await stream.EventAsync("keep.updated")).GetProperty("state").GetString());
    }

    [Fact]
    public async Task ClientApi_EventsPlaybackRouteChangesOnlyReachCurrentDevice()
    {
        var gateway = DeliveryGateway("flac", "flac");
        using var factory = ClientPlaybackFactory(gateway);
        using var client = CreateClientApiClient(factory);
        await SetClientPlaybackAsync(factory, "listener", "phone", "ext-deezer-song-42");
        using var phone = await OpenClientEventsAsync(client);
        using var tablet = await OpenClientEventsAsync(client, device: "tablet");
        using var other = await OpenClientEventsAsync(client, user: "other");
        var principal = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!;
        var execution = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "primary", "listener", principal,
            "fixture", DateTimeOffset.UtcNow.AddMinutes(1), default, new(deviceId: "phone"));
        var activity = factory.Services.GetRequiredService<PlaybackDeliveryActivityStore>();
        ProtocolProviderStream Route(string provider) => new(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) },
            new ProviderStreamLease("fixture", new Uri("https://fixture.invalid/audio"), DateTimeOffset.UtcNow.AddMinutes(1), true, true,
                DeliveryMedia("flac", "flac"), ProviderStreamRetryBehavior.DoNotRetry), provider, "42", SelectionReason: "provider-priority");
        var first = Route("deezer");
        activity.StreamOpened(execution, "ext-deezer-song-42", ProviderAudioQuality.Lossless, first);
        activity.StreamOpened(execution, "ext-deezer-song-42", ProviderAudioQuality.Lossless, first);
        activity.StreamOpened(execution, "ext-deezer-song-42", ProviderAudioQuality.Lossless, Route("qobuz"));
        Assert.Equal("ext-deezer-song-42", (await phone.EventAsync("playback.updated")).GetProperty("itemId").GetString());
        var identities = factory.Services.GetRequiredService<ClientEventHub>();
        var listeners = await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "other"));
        identities.Publish(listeners!.UserId, ClientEvent.Releases(7));
        Assert.Equal("event: releases.updated\ndata: {\"count\":7}", await other.FrameAsync());
        identities.Publish(principal.UserId, ClientEvent.Releases(8));
        Assert.Equal("event: releases.updated\ndata: {\"count\":8}", await tablet.FrameAsync());
    }

    private static async Task<ClientEventStream> OpenClientEventsAsync(HttpClient client, string user = "listener", string? device = "phone")
    {
        using var request = ClientPlaybackRequest("/allstarr/v1/events", user, device);
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, br");
        request.Headers.TryAddWithoutValidation("Last-Event-ID", "previous-session");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = new ClientEventStream(response, new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token)));
        Assert.Equal("event: ready\ndata: {}", await result.FrameAsync());
        return result;
    }

    private sealed class ClientEventBufferingFilter : IStartupFilter
    {
        public bool Disabled { get; private set; }
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((http, continuePipeline) =>
            {
                if (http.Request.Path == "/allstarr/v1/events")
                {
                    var original = http.Features.Get<IHttpResponseBodyFeature>()!;
                    http.Features.Set<IHttpResponseBodyFeature>(new ClientEventBodyFeature(original, () => Disabled = true));
                }
                return continuePipeline();
            });
            next(app);
        };
    }

    private sealed class ClientEventBodyFeature(IHttpResponseBodyFeature inner, Action disabled) : IHttpResponseBodyFeature
    {
        public Stream Stream => inner.Stream;
        public PipeWriter Writer => inner.Writer;
        public void DisableBuffering() { disabled(); inner.DisableBuffering(); }
        public Task StartAsync(CancellationToken cancellationToken = default) => inner.StartAsync(cancellationToken);
        public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default) => inner.SendFileAsync(path, offset, count, cancellationToken);
        public Task CompleteAsync() => inner.CompleteAsync();
    }

    private sealed class ClientEventStream(HttpResponseMessage response, StreamReader reader) : IDisposable
    {
        public HttpResponseMessage Response => response;
        public async Task<string> FrameAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var lines = new List<string>();
            while (await reader.ReadLineAsync(timeout.Token) is { } line)
            {
                if (line.Length == 0) return string.Join('\n', lines);
                lines.Add(line);
            }
            throw new EndOfStreamException();
        }
        public async Task<JsonElement> EventAsync(string type, string? itemId = null)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var frame = await FrameAsync();
                if (!frame.StartsWith("event: " + type + "\n", StringComparison.Ordinal)) continue;
                using var document = JsonDocument.Parse(frame.Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..]);
                if (itemId != null && document.RootElement.GetProperty("itemId").GetString() != itemId) continue;
                return document.RootElement.Clone();
            }
            throw new InvalidOperationException("The expected event was not received.");
        }
        public void Dispose() { response.Dispose(); reader.Dispose(); }
    }
}
