using System.Net;
using System.Text;
using System.Text.Json;
using allstarr.Core.Identity;
using allstarr.Core.Playback;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Jellyfin;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("json", true)]
    [InlineData("xml", true)]
    [InlineData("json", false)]
    [InlineData("xml", false)]
    public async Task SyntheticActivity_SubsonicSuppressesProviderSignalsAndNowPlayingButPreservesNative(string format, bool synthetic)
    {
        var recorded = new List<PlaybackSignalRequest>();
        var relayed = new List<string>();
        using var factory = SubsonicPlaybackFactory(format, recorded, relayed,
            configure: services => InstallSyntheticPrincipal(services, synthetic));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Synthetic-Listener", "true");
        using var start = await client.GetAsync(SubsonicPlaybackUrl("scrobble", format, "Synthetic") +
            "&id=ext-deezer-song-42&submission=false");
        using var progress = await client.PostAsync(SubsonicPlaybackUrl("reportPlayback", format, "Synthetic"),
            new StringContent("mediaId=ext-deezer-song-42&state=playing&positionMs=90000&ignoreScrobble=false",
                Encoding.UTF8, "application/x-www-form-urlencoded"));
        using var nowPlaying = await client.GetAsync(SubsonicPlaybackUrl("getNowPlaying", format, "Synthetic"));
        var body = await nowPlaying.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        Assert.Equal(HttpStatusCode.OK, progress.StatusCode);
        Assert.Equal(!synthetic, body.Contains("ext-deezer-song-42", StringComparison.Ordinal));
        Assert.Contains("native-track", body);
        Assert.Equal(synthetic ? 0 : 2, recorded.Count(item => item.ItemId == "ext-deezer-song-42"));

        using var native = await client.GetAsync(SubsonicPlaybackUrl("scrobble", format, "Synthetic") +
            "&id=native-track&submission=true");
        Assert.Equal(HttpStatusCode.OK, native.StatusCode);
        Assert.Single(recorded, item => item.ItemId == "native-track");
        Assert.Single(relayed, path => path.Contains("scrobble", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SyntheticActivity_JellyfinSuppressesExternalExplicitAndInferredReportsButPreservesNative(bool synthetic)
    {
        var recorded = new List<PlaybackSignalRequest>();
        var forwarded = new List<string>();
        using var factory = SyntheticJellyfinFactory(recorded, forwarded, synthetic);
        using var client = factory.CreateClient();
        var manager = factory.Services.GetRequiredService<JellyfinSessionManager>();
        var key = new JellyfinSessionKey("primary", "user-1", "synthetic-device");
        await manager.RegisterProxiedWebSocketAsync(key);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization",
            "MediaBrowser Client=\"Synthetic\", Device=\"Synthetic\", DeviceId=\"synthetic-device\", Version=\"1\", Token=\"fixture-key\"");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Synthetic-Listener", "true");
        foreach (var (endpoint, session, ticks) in new[]
        {
            ("Sessions/Playing", "first", 0L),
            ("Sessions/Playing/Progress", "second", TimeSpan.FromSeconds(90).Ticks),
            ("Sessions/Playing/Stopped", "second", TimeSpan.FromSeconds(100).Ticks)
        })
        {
            using var response = await client.PostAsync("/" + endpoint, new StringContent(JsonSerializer.Serialize(new
            {
                ItemId = "ext-deezer-song-42",
                PlaySessionId = session,
                PositionTicks = ticks
            }), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
        Assert.Equal(synthetic ? 0 : 5, forwarded.Count);
        Assert.Equal(synthetic ? 0 : 5, recorded.Count);
        if (synthetic) Assert.Null(manager.GetExternalReport(key, includeStopped: true));

        using var native = await client.PostAsync("/Sessions/Playing", new StringContent(
            "{\"ItemId\":\"native-track\",\"PositionTicks\":0}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, native.StatusCode);
        Assert.Contains(recorded, item => item.ItemId == "native-track");
        Assert.Equal("native-track", manager.GetLastPlayingItemId(key));
    }

    [Fact]
    public async Task SyntheticActivity_JellyfinSessionsHideExistingProviderStateAfterPersistedDesignation()
    {
        var recorded = new List<PlaybackSignalRequest>();
        using var factory = SyntheticJellyfinFactory(recorded, [], false);
        using var client = factory.CreateClient();
        var manager = factory.Services.GetRequiredService<JellyfinSessionManager>();
        await manager.RegisterProxiedWebSocketAsync(new("primary", "user-1", "synthetic-device"));
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization",
            "MediaBrowser Client=\"Fixture\", Device=\"Fixture\", DeviceId=\"synthetic-device\", Version=\"1\", Token=\"fixture-key\"");
        using var start = await client.PostAsync("/Sessions/Playing", new StringContent(
            "{\"ItemId\":\"ext-deezer-song-42\",\"PositionTicks\":0}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, start.StatusCode);
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>();
        await using (var db = await contexts.CreateDbContextAsync())
        {
            var now = DateTimeOffset.UtcNow;
            db.Users.Add(new UserRecord
            {
                Id = Guid.Parse("018f1f6e-8e9c-77f5-9a79-3d8a494d60cd"),
                BackendType = "jellyfin",
                BackendInstanceId = "primary",
                BackendPrincipalId = "user-1",
                DisplayName = "Fixture",
                Enabled = true,
                IsSynthetic = true,
                CreatedAt = now,
                UpdatedAt = now,
                LastSeenAt = now
            });
            await db.SaveChangesAsync();
        }

        using var response = await client.GetAsync("/Sessions");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rows = body.RootElement.EnumerateArray().ToArray();
        Assert.False(rows[0].TryGetProperty("NowPlayingItem", out _));
        Assert.Equal("native-track", rows[1].GetProperty("NowPlayingItem").GetProperty("Id").GetString());
    }

    [Theory]
    [InlineData(true, true, "primary", "ext-deezer-song-42", 0)]
    [InlineData(true, false, "primary", "ext-deezer-song-42", 0)]
    [InlineData(false, true, "primary", "ext-deezer-song-42", 1)]
    [InlineData(true, true, "other", "ext-deezer-song-42", 1)]
    [InlineData(true, true, "primary", "native-track", 1)]
    public async Task SyntheticActivity_JellyfinSessionCleanupRechecksPersistedProviderOwner(bool synthetic,
        bool enabled, string backend, string itemId, int expectedStops)
    {
        var forwarded = new List<string>();
        using var factory = SyntheticJellyfinFactory([], forwarded, false);
        using var client = factory.CreateClient();
        var manager = factory.Services.GetRequiredService<JellyfinSessionManager>();
        var key = new JellyfinSessionKey("primary", "user-1", "synthetic-device");
        await manager.RegisterProxiedWebSocketAsync(key);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization",
            "MediaBrowser Client=\"Fixture\", Device=\"Fixture\", DeviceId=\"synthetic-device\", Version=\"1\", Token=\"fixture-key\"");
        using var start = await client.PostAsync("/Sessions/Playing", new StringContent(JsonSerializer.Serialize(new
        {
            ItemId = itemId,
            PositionTicks = 0
        }), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, start.StatusCode);
        Assert.True(manager.HasSession(key));
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>();
        await using (var db = await contexts.CreateDbContextAsync())
        {
            var now = DateTimeOffset.UtcNow;
            db.Users.Add(new UserRecord
            {
                Id = Guid.Parse("018f1f6e-8e9c-77f5-9a79-3d8a494d60cd"),
                BackendType = "jellyfin",
                BackendInstanceId = backend,
                BackendPrincipalId = "user-1",
                DisplayName = "Fixture",
                Enabled = enabled,
                IsSynthetic = synthetic,
                CreatedAt = now,
                UpdatedAt = now,
                LastSeenAt = now
            });
            await db.SaveChangesAsync();
        }
        forwarded.Clear();

        await manager.RemoveSessionAsync(key);

        Assert.Equal(expectedStops, forwarded.Count);
        Assert.False(manager.HasSession(key));
        if (itemId == "native-track")
        {
            using var stop = JsonDocument.Parse(Assert.Single(forwarded));
            Assert.Equal("native-track", stop.RootElement.GetProperty("ItemId").GetString());
        }
    }

    private static ProtocolFactory SyntheticJellyfinFactory(List<PlaybackSignalRequest> recorded, List<string> forwarded, bool synthetic)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42"))
            .ReturnsAsync(new Song { Id = "ext-deezer-song-42", Title = "Provider", Artist = "Artist", Duration = 180 });
        var pipeline = new Mock<IPlaybackSignalPipeline>();
        pipeline.Setup(item => item.RecordAsync(It.IsAny<PlaybackSignalRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PlaybackSignalRequest, CancellationToken>((request, _) => recorded.Add(request)).ReturnsAsync(true);
        return new ProtocolFactory("Jellyfin", request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/Users/Me") return Json(200, "{\"Id\":\"user-1\"}");
            if (path == "/Sessions") return Json(200, JsonSerializer.Serialize(new[]
            {
                new { UserId = "user-1", DeviceId = "synthetic-device", NowPlayingItem = new { Id = JellyfinExternalPlaybackReport.UpstreamId("ext-deezer-song-42") } },
                new { UserId = "other", DeviceId = "native-device", NowPlayingItem = new { Id = "native-track" } }
            }));
            if (path.StartsWith("/Items/", StringComparison.Ordinal)) return Json(200, "{\"Id\":\"native-track\",\"Name\":\"Native\",\"Type\":\"Audio\"}");
            if (path.StartsWith("/Sessions/Playing", StringComparison.Ordinal))
                forwarded.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return Json(204, "{}");
        }, services =>
        {
            AddArtworkPrincipal(services, "user-1");
            InstallSyntheticPrincipal(services, synthetic);
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
            services.RemoveAll<IPlaybackSignalPipeline>();
            services.AddSingleton(pipeline.Object);
        });
    }

    private static void InstallSyntheticPrincipal(IServiceCollection services, bool synthetic)
    {
        foreach (var descriptor in services.Where(item => item.ImplementationType == typeof(SonicPrincipalStartupFilter) ||
                     item.ImplementationInstance is ArtworkPrincipalStartupFilter).ToArray()) services.Remove(descriptor);
        services.AddSingleton<IStartupFilter>(new SyntheticPrincipalStartupFilter(synthetic));
    }

    private sealed class SyntheticPrincipalStartupFilter(bool synthetic) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuePipeline) =>
            {
                var subsonic = context.Request.Path.StartsWithSegments("/rest");
                context.Items[BackendIdentityResolver.HttpContextPrincipalItemKey] = new AllstarrPrincipal(
                    Guid.Parse("018f1f6e-8e9c-77f5-9a79-3d8a494d60cd"), subsonic ? "subsonic" : "jellyfin",
                    "primary", subsonic ? "fixture" : "user-1", "Fixture", false, synthetic);
                await continuePipeline();
            });
            next(app);
        };
    }
}
