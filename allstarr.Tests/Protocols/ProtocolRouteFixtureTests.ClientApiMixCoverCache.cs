using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task ClientApi_MixCoverVersionDiskHitSkipsMatchingAndLibraryReadsAndStillChecksAccess()
    {
        var matches = 0;
        var artwork = 0;
        var backend = 0;
        var reads = new MixCoverLibraryReads();
        var library = MixCoverLibrary(async () =>
        {
            Interlocked.Increment(ref matches);
            await Task.Delay(1200);
        });
        using var factory = ClientMixFactory(new(), configure: services =>
        {
            UseTimingImageCache(services);
            ConfigureMixCoverReads(services, library, reads);
            services.RemoveAll<MixCoverImageService>();
            services.AddSingleton(provider => new MixCoverImageService(MixCoverRenderer.Render, (_, _) =>
            {
                Interlocked.Increment(ref artwork);
                return Task.FromResult<byte[]?>(MixPicture());
            }, cache: provider.GetRequiredService<IApplicationCache>()));
        }, observe: request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("Items", StringComparison.Ordinal)) Interlocked.Increment(ref backend);
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var mix = await SeedClientMixAsync(factory);
        var path = $"/allstarr/v1/mixes/{mix.Id}/cover";
        using var first = await client.GetAsync(path);
        first.EnsureSuccessStatusCode();
        var cache = factory.Services.GetRequiredService<IApplicationCache>();
        await cache.DeleteByPatternAsync("feed:read:v1:*");
        matches = reads.Count = backend = 0;
        var timer = Stopwatch.StartNew();
        using var before = await client.GetAsync(path);
        before.EnsureSuccessStatusCode();
        var beforeMs = timer.Elapsed.TotalMilliseconds;
        var beforeMatches = matches;
        Assert.Equal(1, beforeMatches);
        await cache.DeleteByPatternAsync("feed:read:v1:*");
        matches = reads.Count = backend = 0;
        var versioned = path + "?v=" + MixCoverImageService.Version(MixCoverInput.From(mix));
        timer.Restart();
        using var after = await client.GetAsync(versioned);
        after.EnsureSuccessStatusCode();
        var afterMs = timer.Elapsed.TotalMilliseconds;
        Assert.Equal(await before.Content.ReadAsByteArrayAsync(), await after.Content.ReadAsByteArrayAsync());
        Assert.Equal(before.Headers.ETag, after.Headers.ETag);
        Assert.Equal(TimeSpan.FromDays(365), after.Headers.CacheControl?.MaxAge);
        Assert.True(after.Headers.CacheControl?.Private);
        Assert.Contains("immutable", after.Headers.CacheControl!.ToString());
        var timing = Assert.Single(after.Headers.GetValues("Server-Timing"));
        Assert.DoesNotContain(", match;", timing);
        Assert.DoesNotContain(", build;", timing);
        Assert.Equal(0, matches);
        Assert.Equal(0, reads.Count);
        Assert.Equal(0, backend);
        Assert.Equal(1, artwork);
        _lyricsSearchOutput.WriteLine($"Mix cover, 1200 ms matching and rendered bytes on disk: unversioned {beforeMs:F2} ms / {beforeMatches} matches; versioned {afterMs:F2} ms / {matches} matches / {reads.Count} library reads / {backend} backend reads. {timing}");

        using var conditional = new HttpRequestMessage(HttpMethod.Get, versioned);
        conditional.Headers.IfNoneMatch.Add(after.Headers.ETag!);
        using var unchanged = await client.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        Assert.Empty(await unchanged.Content.ReadAsByteArrayAsync());
        Assert.Equal(after.Headers.CacheControl, unchanged.Headers.CacheControl);
        Assert.Equal(after.Headers.ETag, unchanged.Headers.ETag);
        Assert.Equal(0, matches);
        Assert.Equal(0, reads.Count);

        client.DefaultRequestHeaders.Remove("X-Emby-Token");
        client.DefaultRequestHeaders.Add("X-Emby-Token", "other-token");
        using var foreign = new HttpRequestMessage(HttpMethod.Get, versioned);
        foreign.Headers.IfNoneMatch.Add(after.Headers.ETag!);
        using var hidden = await client.SendAsync(foreign);
        await AssertClientApiError(hidden, 404, "not_found");
        Assert.Equal(0, matches);
        Assert.Equal(0, reads.Count);
    }

    [Fact]
    public async Task ClientApi_MixCoverReusesListInputsUntilTheStoredMixChanges()
    {
        var matches = 0;
        var reads = new MixCoverLibraryReads();
        var library = MixCoverLibrary(() => { matches++; return Task.CompletedTask; });
        using var factory = ClientMixFactory(new(), configure: services =>
        {
            ConfigureMixCoverReads(services, library, reads);
            services.RemoveAll<MixCoverImageService>();
            services.AddSingleton(provider => new MixCoverImageService(_ => [9], (_, _) => Task.FromResult<byte[]?>(MixPicture()),
                cache: provider.GetRequiredService<IApplicationCache>()));
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var mix = await SeedClientMixAsync(factory);
        using var list = await client.GetAsync("/allstarr/v1/mixes");
        list.EnsureSuccessStatusCode();
        Assert.Equal(1, matches);
        matches = reads.Count = 0;
        using var cover = await client.GetAsync($"/allstarr/v1/mixes/{mix.Id}/cover");
        cover.EnsureSuccessStatusCode();
        Assert.Equal(0, matches);
        Assert.Equal(0, reads.Count);
        Assert.Equal(TimeSpan.Zero, cover.Headers.CacheControl?.MaxAge);

        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        (await db.LibraryTracks.SingleAsync()).Revision++;
        await db.SaveChangesAsync();
        using var libraryChanged = await client.GetAsync($"/allstarr/v1/mixes/{mix.Id}/cover");
        libraryChanged.EnsureSuccessStatusCode();
        Assert.Equal(1, matches);
        Assert.Equal(cover.Headers.ETag, libraryChanged.Headers.ETag);
        matches = 0;
        var saved = await db.ExternalMetadataSnapshots.AsNoTracking().SingleAsync(row => row.ResourceKind == "personal-mixes");
        var document = JsonSerializer.Deserialize<PersonalMixDocument>(saved.PayloadJson)!;
        document.Mixes["custom"] = mix with { Generation = mix.Generation + 1 };
        saved.Id = Guid.NewGuid();
        saved.SnapshotVersion++;
        saved.PayloadJson = JsonSerializer.Serialize(document);
        db.ExternalMetadataSnapshots.Add(saved);
        await db.SaveChangesAsync();
        using var updated = await client.GetAsync($"/allstarr/v1/mixes/{mix.Id}/cover");
        updated.EnsureSuccessStatusCode();
        Assert.Equal(1, matches);
        Assert.NotEqual(cover.Headers.ETag, updated.Headers.ETag);

        document.Mixes.Clear();
        db.Entry(saved).State = EntityState.Detached;
        saved.Id = Guid.NewGuid();
        saved.SnapshotVersion++;
        saved.PayloadJson = JsonSerializer.Serialize(document);
        db.ExternalMetadataSnapshots.Add(saved);
        await db.SaveChangesAsync();
        using var deleted = await client.GetAsync($"/allstarr/v1/mixes/{mix.Id}/cover?v={MixCoverImageService.Version(MixCoverInput.From(mix))}");
        await AssertClientApiError(deleted, 404, "not_found");
    }

    [Fact]
    public async Task ClientApi_MixCoverStoredServiceCardChecksTheAccountWithoutReadingSongs()
    {
        var reads = new MixCoverLibraryReads();
        var library = new Mock<ILibraryIndexService>(MockBehavior.Strict);
        using var factory = ClientMixFactory(new(), configure: services =>
        {
            ConfigureMixCoverReads(services, library, reads);
            services.RemoveAll<MixCoverImageService>();
            services.AddSingleton(provider => new MixCoverImageService(_ => throw new InvalidOperationException("Unexpected render"),
                cache: provider.GetRequiredService<IApplicationCache>(), serviceArt: (_, _, _) =>
                    Task.FromResult<ResolvedMediaAsset?>(new(MixPicture(), "image/png", "hash", null, null, false))));
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var owner = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!.UserId;
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        var user = await db.Users.SingleAsync(item => item.Id == owner);
        var account = new ProviderAccountRecord { Id = Guid.NewGuid(), OwnerUserId = owner, ProviderId = "spotify", Enabled = true, Revision = 1 };
        db.ProviderAccounts.Add(account);
        var card = new PersonalMix(Guid.NewGuid(), "external", "Discover Weekly", "From Spotify", 1, null,
            new(true, "Ready"), false, [])
        { Source = "spotify", SourcePlaylistId = "weekly", ProviderAccountId = account.Id, SongCount = 30, ArtworkUrl = "saved-art" };
        var scope = Hash($"{user.Id}:{user.BackendType}:{user.BackendInstanceId}:{user.BackendPrincipalId}");
        var json = JsonSerializer.Serialize(new[] { card });
        db.ExternalMetadataSnapshots.Add(new()
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            ProviderAccountId = account.Id,
            ProviderId = "spotify",
            ResourceKind = "service-feeds",
            ExternalIdHash = Hash(scope + ":1"),
            SnapshotVersion = 1,
            Protocol = user.BackendType,
            BackendInstanceId = user.BackendInstanceId,
            BackendPrincipalId = user.BackendPrincipalId,
            ProviderRevision = "1",
            PayloadJson = json,
            PayloadSha256 = Hash(json),
            RetrievedAt = DateTimeOffset.UtcNow
        });
        db.PlaylistLinks.Add(new()
        {
            Id = card.Id,
            OwnerUserId = owner,
            ProviderAccountId = account.Id,
            SourceProviderId = "spotify",
            SourcePlaylistId = "weekly",
            SourcePlaylistIdHash = Hash("weekly"),
            TargetProtocol = user.BackendType,
            TargetBackendInstanceId = user.BackendInstanceId,
            Enabled = true,
            RuleVersion = "1",
            PolicyVersion = "1",
            Mode = PlaylistLinkMode.Virtual
        });
        await db.SaveChangesAsync();
        var input = MixCoverInput.From(card);
        var context = new DefaultHttpContext();
        await factory.Services.GetRequiredService<MixCoverImageService>().CreateResponseAsync(input, context, owner: owner);
        reads.Count = 0;
        var path = $"/allstarr/v1/mixes/{card.Id}/cover?v={MixCoverImageService.Version(input)}";
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        Assert.Equal(MixPicture(), await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(TimeSpan.FromSeconds(300), response.Headers.CacheControl?.MaxAge);
        Assert.Equal(context.Response.Headers.ETag.ToString(), response.Headers.ETag?.Tag);
        Assert.Equal(0, reads.Count);
        library.VerifyNoOtherCalls();
        account.Enabled = false;
        await db.SaveChangesAsync();
        using var disabled = await client.GetAsync(path);
        await AssertClientApiError(disabled, 404, "not_found");

        static string Hash(string value) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    }

    [Fact]
    public async Task ClientApi_MixCoverSixteenMissingVersionRequestsShareResolutionAndRender()
    {
        var matches = 0;
        var loads = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var library = MixCoverLibrary(async () => { Interlocked.Increment(ref matches); await Task.Delay(150); });
        using var factory = ClientMixFactory(new(), configure: services =>
        {
            services.RemoveAll<ILibraryIndexService>();
            services.AddSingleton(library.Object);
            services.RemoveAll<MixCoverImageService>();
            services.AddSingleton(provider => new MixCoverImageService(_ => [9], async (_, _) =>
            {
                Interlocked.Increment(ref loads);
                entered.TrySetResult();
                await release.Task;
                return MixPicture();
            }, cache: provider.GetRequiredService<IApplicationCache>()));
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var mix = await SeedClientMixAsync(factory);
        var path = $"/allstarr/v1/mixes/{mix.Id}/cover?v=missing&width=300&format=jpeg";
        var requests = Enumerable.Range(0, 16).Select(_ => client.GetAsync(path)).ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        release.SetResult();
        var responses = await Task.WhenAll(requests);
        try
        {
            var bytes = MixCoverRenderer.RenderCollage([MixPicture()]);
            foreach (var response in responses)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
                Assert.Equal(TimeSpan.Zero, response.Headers.CacheControl?.MaxAge);
                Assert.Equal(responses[0].Headers.ETag, response.Headers.ETag);
            }
            Assert.Equal(1, matches);
            Assert.Equal(1, loads);
            _lyricsSearchOutput.WriteLine($"16 concurrent missing-version covers: {matches} matching call; {loads} collage render; identical bytes and validators.");
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    private static Mock<ILibraryIndexService> MixCoverLibrary(Func<Task> match)
    {
        var library = new Mock<ILibraryIndexService>(MockBehavior.Strict);
        library.Setup(service => service.FindKnownRecordingTracksAsync(It.IsAny<ProtocolExecutionContext>(),
            It.IsAny<IReadOnlyList<LibraryRecordingLookup>>(), It.IsAny<CancellationToken>())).Returns(async () =>
            {
                await match();
                return new Dictionary<string, string>();
            });
        return library;
    }

    private static void ConfigureMixCoverReads(IServiceCollection services, Mock<ILibraryIndexService> library, MixCoverLibraryReads reads)
    {
        services.RemoveAll<ILibraryIndexService>();
        services.AddSingleton(library.Object);
        services.RemoveAll<IDbContextFactory<AllstarrDbContext>>();
        services.AddSingleton<IDbContextFactory<AllstarrDbContext>>(provider => new AdminAuthTestDbContextFactory(
            new DbContextOptionsBuilder<AllstarrDbContext>(provider.GetRequiredService<DbContextOptions<AllstarrDbContext>>())
                .AddInterceptors(reads).Options));
    }

    private sealed class MixCoverLibraryReads : DbCommandInterceptor
    {
        internal int Count;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("library_tracks", StringComparison.OrdinalIgnoreCase) ||
                command.CommandText.Contains("track_matches", StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref Count);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
