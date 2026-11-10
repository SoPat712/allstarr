using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Intelligence;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Playback;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientApi_PlayActivityAndImportStatusArePersonalAndReadOnly(bool administrator)
    {
        var access = new Mock<IBackendLibraryAccessResolver>();
        access.Setup(service => service.ResolveAsync(It.IsAny<ProtocolExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BackendLibraryAccess(true, []));
        using var factory = ClientMeFactory(administrator, services =>
        {
            services.RemoveAll<IBackendLibraryAccessResolver>();
            services.AddSingleton(access.Object);
        });
        using var client = CreateClientApiClient(factory);
        var (owner, other) = await SeedClientMeAsync(factory, administrator);
        var ownTrace = Guid.CreateVersion7();
        var otherTrace = Guid.CreateVersion7();
        var ownImport = Guid.NewGuid();
        var otherImport = Guid.NewGuid();
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            foreach (var listener in new[] { (Owner: owner, Trace: ownTrace, Import: ownImport, Title: "Own private data"),
                (Owner: other, Trace: otherTrace, Import: otherImport, Title: "Other private data") })
            {
                var at = DateTimeOffset.UtcNow.AddMinutes(-1);
                var trace = new PlayTraceData(listener.Owner, new("Jellyfin", "native", "app", "device", new(listener.Title)), new());
                db.AuditEvents.Add(new()
                {
                    Id = listener.Trace,
                    ActorUserId = listener.Owner,
                    Category = PlaybackStartTimingStore.Category,
                    SubjectType = PlaybackStartTimingStore.Category,
                    SubjectId = "recent",
                    CreatedAt = at,
                    DetailsJson = JsonSerializer.Serialize(new PlaybackStartSample(listener.Trace, at, "completed", null,
                        listener.Title, null, null, false, 100, 50, 75, [], 0, trace))
                });
                db.ListeningHistoryImports.Add(new()
                {
                    Id = listener.Import,
                    OwnerUserId = listener.Owner,
                    Protocol = "jellyfin",
                    BackendInstanceId = "primary",
                    DisplayFileName = listener.Title + ".json",
                    ContentSha256 = listener.Import.ToString("N"),
                    SizeBytes = 1,
                    PreviewJson = "{}",
                    State = ListeningHistoryImportState.Previewed,
                    ExpiresAt = at.AddDays(-1),
                    CreatedAt = at,
                    UpdatedAt = at,
                    PreviewRevision = "fixture",
                    Revision = 1
                });
                var recording = Guid.NewGuid();
                db.CanonicalRecordings.Add(new()
                {
                    Id = recording,
                    CreatedByUserId = listener.Owner,
                    Title = listener.Title,
                    PublicId = CanonicalRecordingRecord.DefaultPublicId(recording)
                });
                db.ListeningEvents.Add(new()
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = listener.Owner,
                    Protocol = "jellyfin",
                    BackendInstanceId = "primary",
                    OccurrenceKey = listener.Import.ToString("N"),
                    SourceKind = "import",
                    Title = listener.Title,
                    CanonicalRecordingId = recording,
                    State = ListeningEventState.Completed,
                    ListenedAt = at,
                    UpdatedAt = at
                });
            }
            await db.SaveChangesAsync();
        }
        foreach (var listener in new[] { (Token: "listener-token", Title: "Own private data", Trace: ownTrace, HiddenTrace: otherTrace,
            Import: ownImport, HiddenImport: otherImport), (Token: "other-token", Title: "Other private data", Trace: otherTrace,
            HiddenTrace: ownTrace, Import: otherImport, HiddenImport: ownImport) })
        {
            client.DefaultRequestHeaders.Remove("X-Emby-Token");
            client.DefaultRequestHeaders.Add("X-Emby-Token", listener.Token);
            foreach (var path in new[] { "plays", "plays/" + listener.Trace, "history/imports", "history/imports/" + listener.Import })
            {
                using var response = await client.GetAsync("/allstarr/v1/me/" + path);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.True(response.Headers.CacheControl?.NoStore);
                var body = await response.Content.ReadAsStringAsync();
                Assert.Contains(listener.Title, body);
                Assert.DoesNotContain(listener.Title == "Own private data" ? "Other private data" : "Own private data", body);
            }
            foreach (var path in new[] { "plays/" + listener.HiddenTrace, "plays?before=" + listener.HiddenTrace, "history/imports/" + listener.HiddenImport })
            {
                using var response = await client.GetAsync("/allstarr/v1/me/" + path);
                await AssertClientApiError(response, 404, "not_found");
            }
            using var older = await client.GetAsync("/allstarr/v1/me/plays?before=" + listener.Trace);
            Assert.Equal(HttpStatusCode.OK, older.StatusCode);
            Assert.Equal("[]", await older.Content.ReadAsStringAsync());
            using var recheck = await client.GetAsync("/allstarr/v1/me/history/imports/recheck");
            Assert.Equal(HttpStatusCode.OK, recheck.StatusCode);
            using var recheckBody = JsonDocument.Parse(await recheck.Content.ReadAsStringAsync());
            Assert.Equal(1, recheckBody.RootElement.GetProperty("songs").GetInt32());
        }
        await using var check = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        Assert.All(await check.ListeningHistoryImports.ToArrayAsync(), item => Assert.Equal(ListeningHistoryImportState.Previewed, item.State));
    }

    [Fact]
    public async Task ClientApi_HistoryUsesTheRecordingIdWithoutALibraryCopyAndCountsQualifiedPlays()
    {
        var providers = new Mock<IProtocolProviderGateway>();
        providers.Setup(service => service.GetPlayableProviderOrderAsync(It.IsAny<ProviderActorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "deezer" });
        var index = new Mock<ILibraryIndexService>();
        index.Setup(service => service.FindKnownRecordingTracksAsync(It.IsAny<ProtocolExecutionContext>(),
                It.IsAny<IReadOnlyList<LibraryRecordingLookup>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());
        using var factory = ClientDetailsFactory(_ => Json(200, """{"Id":"listener"}"""), services =>
        {
            services.RemoveAll<ILibraryIndexService>();
            services.AddSingleton(index.Object);
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(providers.Object);
        });
        using var client = CreateClientApiClient(factory);
        var recording = await SeedClientDetailsAsync(factory, "listener", "route", personal: false);
        var owner = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!.UserId;
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            foreach (var seconds in new[] { 29, 30 })
                db.ListeningEvents.Add(new()
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = owner,
                    Protocol = "jellyfin",
                    BackendInstanceId = "primary",
                    OccurrenceKey = seconds.ToString(),
                    State = ListeningEventState.Skipped,
                    StartedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    PositionTicks = seconds * TimeSpan.TicksPerSecond,
                    SourceKind = "import",
                    TrackReference = "archive:route",
                    CanonicalRecordingId = recording,
                    Title = "Qualified play",
                    Artist = "Artist"
                });
            await db.SaveChangesAsync();
        }
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var response = await client.GetAsync("/allstarr/v1/me/history");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var play = Assert.Single(body.RootElement.GetProperty("plays").EnumerateArray());
        Assert.Equal(CanonicalRecordingRecord.DefaultPublicId(recording), play.GetProperty("itemId").GetString());
        Assert.True(play.GetProperty("isExternal").GetBoolean());
        providers.Setup(service => service.GetPlayableProviderOrderAsync(It.IsAny<ProviderActorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());
        using var unavailable = await client.GetAsync("/allstarr/v1/me/history");
        using var unavailableBody = JsonDocument.Parse(await unavailable.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, Assert.Single(unavailableBody.RootElement.GetProperty("plays").EnumerateArray()).GetProperty("itemId").ValueKind);
        using var details = await client.PostAsJsonAsync("/allstarr/v1/items/details", new { ids = new[] { "ext-deezer-song-route" } });
        using var detailsBody = JsonDocument.Parse(await details.Content.ReadAsStringAsync());
        Assert.Equal(1, Assert.Single(detailsBody.RootElement.GetProperty("items").EnumerateArray()).GetProperty("playCount").GetInt64());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientApi_DashboardAddressIsOptionalAndUsesTheConfiguredPublicAddress(bool configured)
    {
        using var factory = ClientMeFactory();
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        if (configured)
            await factory.Services.GetRequiredService<IDurableRuntimeSettings>().ApplyBatchAsync(
                [new("Admin:Oidc:PublicUrl", "https://dashboard.example.test/control", null)], "fixture");
        using var response = await client.GetAsync("/allstarr/v1/capabilities");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("features").GetProperty("dashboardLink").GetBoolean());
        Assert.Equal(configured, body.RootElement.TryGetProperty("dashboard", out var dashboard));
        if (configured) Assert.Equal("https://dashboard.example.test/control", dashboard.GetProperty("url").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientApi_HistoryAndCountsIncludeImportsAndStayPersonal(bool administrator)
    {
        var index = new Mock<ILibraryIndexService>();
        var scopes = new List<ProtocolExecutionContext>();
        index.Setup(service => service.FindKnownRecordingTracksAsync(It.IsAny<ProtocolExecutionContext>(),
                It.IsAny<IReadOnlyList<LibraryRecordingLookup>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProtocolExecutionContext context, IReadOnlyList<LibraryRecordingLookup> lookups, CancellationToken _) =>
            {
                scopes.Add(context);
                return lookups.Where(item => item.Isrc == "USABC1200001").ToDictionary(item => item.PublicId, _ => "native");
            });
        using var factory = ClientDetailsFactory(request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, request.Headers.ToString().Contains("other-token", StringComparison.Ordinal)
                ? """{"Id":"other"}""" : JsonSerializer.Serialize(new { Id = "listener", Policy = new { IsAdministrator = administrator } }))
            : Json(200, """{"Items":[{"Id":"native","Type":"Audio"},{"Id":"unplayed","Type":"Audio"},{"Id":"album","Type":"MusicAlbum"}]}"""), services =>
        {
            services.RemoveAll<ILibraryIndexService>();
            services.AddSingleton(index.Object);
        });
        using var client = CreateClientApiClient(factory);
        var (owner, other) = await SeedClientMeAsync(factory, administrator);
        var recording = await SeedClientDetailsAsync(factory, "listener", "history", personal: false);
        var at = DateTimeOffset.Parse("2026-01-02T03:04:05.1234567Z");
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            db.LibraryTracks.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                Protocol = "jellyfin",
                BackendInstanceId = "primary",
                BackendItemId = "native",
                CanonicalRecordingId = recording,
                Title = "Own play",
                Artist = "Own artist"
            });
            foreach (var entry in new[] { (Owner: owner, Title: "Own play", Reference: "ext-deezer-song-history", Recording: (Guid?)recording),
                (Owner: owner, Title: "Imported without item", Reference: "archive:missing", Recording: (Guid?)null),
                (Owner: owner, Title: "Native play", Reference: "native", Recording: (Guid?)recording),
                (Owner: other, Title: "Other private play", Reference: "native", Recording: (Guid?)recording) })
                db.ListeningEvents.Add(new()
                {
                    Id = Guid.CreateVersion7(),
                    OwnerUserId = entry.Owner,
                    Protocol = "jellyfin",
                    BackendInstanceId = "primary",
                    OccurrenceKey = Guid.NewGuid().ToString("N"),
                    State = ListeningEventState.Completed,
                    ListenedAt = at,
                    UpdatedAt = at,
                    SourceKind = "import",
                    ImportProvenance = "fixture-import",
                    TrackReference = entry.Reference,
                    CanonicalRecordingId = entry.Recording,
                    Title = entry.Title,
                    Artist = "Own artist",
                    DurationMilliseconds = 120000,
                    Isrc = entry.Recording.HasValue ? "USABC1200001" : null
                });
            await db.SaveChangesAsync();
        }
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var plays = new List<JsonElement>();
        string? cursor = null;
        do
        {
            using var response = await client.GetAsync("/allstarr/v1/me/history?limit=1" + (cursor == null ? "" : "&before=" + Uri.EscapeDataString(cursor)));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            plays.AddRange(body.RootElement.GetProperty("plays").EnumerateArray().Select(item => item.Clone()));
            cursor = body.RootElement.TryGetProperty("next", out var next) ? next.GetString() : null;
            Assert.True(plays.Count <= 5);
        } while (cursor != null);
        Assert.Equal(5, plays.Count);
        Assert.Equal(5, plays.Select(item => item.GetProperty("title").GetString()).Distinct().Count());
        Assert.DoesNotContain(plays, item => item.GetProperty("title").GetString() == "Other private play");
        Assert.Equal("native", plays.Single(item => item.GetProperty("title").GetString() == "Own play").GetProperty("itemId").GetString());
        Assert.False(plays.Single(item => item.GetProperty("title").GetString() == "Own play").GetProperty("isExternal").GetBoolean());
        Assert.Equal(JsonValueKind.Null, plays.Single(item => item.GetProperty("title").GetString() == "Imported without item").GetProperty("itemId").ValueKind);
        Assert.Equal(at, plays[0].GetProperty("playedAt").GetDateTimeOffset());
        Assert.All(plays, item => Assert.StartsWith("/allstarr/v1/me/stats/art?", item.GetProperty("artworkUrl").GetString()));
        Assert.All(scopes, context => Assert.False(context.Principal!.IsAdministrator));

        using var counts = await client.PostAsJsonAsync("/allstarr/v1/items/details", new { ids = new[] { "native", "ext-deezer-song-history", "unplayed", "album" } });
        Assert.Equal(HttpStatusCode.OK, counts.StatusCode);
        using var countsBody = JsonDocument.Parse(await counts.Content.ReadAsStringAsync());
        var items = countsBody.RootElement.GetProperty("items").EnumerateArray().ToArray();
        foreach (var id in new[] { "native", "ext-deezer-song-history" })
        {
            var item = items.Single(item => item.GetProperty("id").GetString() == id);
            Assert.Equal(2, item.GetProperty("playCount").GetInt64());
            Assert.Equal(at, item.GetProperty("lastPlayedAt").GetDateTimeOffset());
        }
        var unplayed = items.Single(item => item.GetProperty("id").GetString() == "unplayed");
        Assert.Equal(0, unplayed.GetProperty("playCount").GetInt64());
        Assert.False(unplayed.TryGetProperty("lastPlayedAt", out _));
        Assert.False(items.Single(item => item.GetProperty("id").GetString() == "album").TryGetProperty("playCount", out _));
        client.DefaultRequestHeaders.Remove("X-Emby-Token");
        client.DefaultRequestHeaders.Add("X-Emby-Token", "other-token");
        using var otherHistory = await client.GetAsync("/allstarr/v1/me/history");
        using var otherBody = JsonDocument.Parse(await otherHistory.Content.ReadAsStringAsync());
        Assert.Contains(otherBody.RootElement.GetProperty("plays").EnumerateArray(), item => item.GetProperty("title").GetString() == "Other private play");
        Assert.DoesNotContain("Imported without item", otherBody.RootElement.GetRawText());
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=201")]
    [InlineData("before=invalid")]
    public async Task ClientApi_HistoryRejectsInvalidPaging(string query)
    {
        using var factory = ClientMeFactory();
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var response = await client.GetAsync("/allstarr/v1/me/history?" + query);
        await AssertClientApiError(response, 400, "invalid_request");
    }
}
