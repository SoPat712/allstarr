using allstarr.Core.Caching;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using allstarr.Core.Music;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task PlaylistSpeedRoundUsesThreeHundredMixedSongsAndReportsWarmStages()
    {
        var reads = new PlaylistSpeedReads();
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(service => service.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", It.IsAny<string>()))
            .ReturnsAsync((ProtocolExecutionContext _, string _, string id) => new Song
            { Id = "ext-deezer-song-" + id, Title = "Song " + id, Artist = "Artist", Album = "Album", Duration = 180, ExternalProvider = "deezer", ExternalId = id });
        using var factory = ClientMixFactory(gateway, configure: services =>
        {
            services.RemoveAll<IDbContextFactory<AllstarrDbContext>>();
            services.AddSingleton<IDbContextFactory<AllstarrDbContext>>(provider => new AdminAuthTestDbContextFactory(
                new DbContextOptionsBuilder<AllstarrDbContext>(provider.GetRequiredService<DbContextOptions<AllstarrDbContext>>()).AddInterceptors(reads).Options));
            services.RemoveAll<MixCoverImageService>();
            services.AddSingleton(provider => new MixCoverImageService(MixCoverRenderer.Render, (_, _) => Task.FromResult<byte[]?>(MixPicture()),
                cache: provider.GetRequiredService<IApplicationCache>()));
        }, nativeResponse: request =>
        {
            if (request.RequestUri!.AbsolutePath != "/Items") return null;
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
            var ids = query["Ids"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (ids.Length == 0) return Json(200, """{"Items":[],"TotalRecordCount":0}""");
            return Json(200, JsonSerializer.Serialize(new { Items = ids.Select(id => new { Id = id, Type = "Audio", Name = "Song " + id, ServerId = "server", RunTimeTicks = 1800000000L }), TotalRecordCount = ids.Length }));
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var mix = await SeedClientMixAsync(factory);
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        var row = await db.ExternalMetadataSnapshots.AsNoTracking().SingleAsync(item => item.ResourceKind == "personal-mixes");
        var document = JsonSerializer.Deserialize<PersonalMixDocument>(row.PayloadJson)!;
        mix = mix with
        {
            Songs = Enumerable.Range(0, 300).Select(index => new PersonalMixItem(mix.Id, 2,
            new Song
            {
                Id = index % 2 == 0 ? "native-" + index : "ext-deezer-song-" + index,
                Title = "Song " + index,
                Artist = "Artist",
                Album = "Album " + index,
                Duration = 180,
                IsLocal = index % 2 == 0,
                ExternalProvider = index % 2 == 0 ? null : "deezer",
                ExternalId = index % 2 == 0 ? null : index.ToString(CultureInfo.InvariantCulture)
            },
            index % 2 == 0 ? "library" : "deezer", "Picked for you", 1)).ToArray()
        };
        document.Mixes["custom"] = mix;
        // Include the other saved mixes in the listener's document, as on an established account.
        for (var index = 0; index < 11; index++) document.Mixes["saved-" + index] = mix with { Id = Guid.NewGuid(), Songs = mix.Songs.Take(50).ToArray() };
        row.Id = Guid.NewGuid();
        row.SnapshotVersion++;
        row.PayloadJson = JsonSerializer.Serialize(document, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        db.ExternalMetadataSnapshots.Add(row);
        foreach (var song in mix.Songs.Where(item => item.Song.IsLocal)) db.LibraryTracks.Add(new()
        { Id = Guid.NewGuid(), OwnerUserId = row.OwnerUserId, Protocol = "jellyfin", BackendInstanceId = "primary", BackendLibraryId = "library", BackendItemId = song.Song.Id, Revision = 1 });
        await db.SaveChangesAsync();
        var playlist = PlaylistVirtualizationService.CreateProtocolId(mix.Id);
        var paths = new[]
        {
            $"/Playlists/{playlist}/Items?userId=listener&startIndex=0&limit=50",
            $"/Items/{playlist}?userId=listener",
            $"/Items/{playlist}/Images/Primary?fillWidth=600&quality=90&tag={MixCoverImageService.GetTag(MixCoverInput.From(mix))}",
            "/Items?userId=listener&limit=0&recursive=true&includeItemTypes=Playlist&enableTotalRecordCount=true",
            $"/allstarr/v1/mixes/{mix.Id}/cover?v={MixCoverImageService.Version(MixCoverInput.From(mix))}"
        };
        foreach (var path in paths)
        {
            using (var cold = await client.GetAsync(path))
            {
                cold.EnsureSuccessStatusCode();
                _lyricsSearchOutput.WriteLine($"Playlist first {path}: {Assert.Single(cold.Headers.GetValues("Server-Timing"))}");
            }
            var timings = new List<(double Ms, string Header, int Reads)>();
            byte[]? previous = null;
            for (var sample = 0; sample < 3; sample++)
            {
                reads.Commands.Clear();
                reads.SavedMixReads = reads.ForkReads = reads.FavoriteReads = 0;
                using var response = await client.GetAsync(path);
                response.EnsureSuccessStatusCode();
                var bytes = await response.Content.ReadAsByteArrayAsync();
                if (previous != null) Assert.Equal(previous, bytes);
                previous = bytes;
                if (!path.StartsWith("/Items?", StringComparison.Ordinal))
                    Assert.Equal(0, reads.SavedMixReads);
                Assert.Equal(0, reads.ForkReads);
                if (path.StartsWith("/Items/allstarr-vpl-", StringComparison.Ordinal)) Assert.Equal(0, reads.FavoriteReads);
                var header = Assert.Single(response.Headers.GetValues("Server-Timing"));
                var total = double.Parse(header.Split(',')[0].Split('=')[1], CultureInfo.InvariantCulture);
                timings.Add((total, header, reads.Commands.Count));
            }
            var median = timings.OrderBy(item => item.Ms).ElementAt(1);
            var report = $"Playlist speed {path}: {median.Ms:F1} ms; {median.Reads} SQL reads; {median.Header}";
            _lyricsSearchOutput.WriteLine(report);

            reads.Commands.Clear();
            reads.SavedMixReads = reads.ForkReads = reads.FavoriteReads = 0;
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = Enumerable.Range(0, 12).Select(async _ =>
            {
                await start.Task;
                using var response = await client.GetAsync(path);
                response.EnsureSuccessStatusCode();
                Assert.Equal(previous, await response.Content.ReadAsByteArrayAsync());
                var header = Assert.Single(response.Headers.GetValues("Server-Timing"));
                return double.Parse(header.Split(',')[0].Split('=')[1], CultureInfo.InvariantCulture);
            }).ToArray();
            start.SetResult();
            var parallel = (await Task.WhenAll(requests)).Order().ToArray();
            Assert.Equal(0, reads.SavedMixReads);
            _lyricsSearchOutput.WriteLine($"Playlist parallel {path}: 12 requests; median {(parallel[5] + parallel[6]) / 2:F1} ms; max {parallel[^1]:F1} ms; {reads.Commands.Count} SQL reads.");
        }

        reads.Commands.Clear();
        reads.SavedMixReads = reads.ForkReads = reads.FavoriteReads = 0;
        using var later = await client.GetAsync($"/Playlists/{playlist}/Items?userId=listener&startIndex=50&limit=50");
        later.EnsureSuccessStatusCode();
        Assert.Equal(0, reads.SavedMixReads);
        Assert.Equal(0, reads.ForkReads);
    }

    [Fact]
    public async Task PlaylistCountOnlyMatchesBrowseWithoutReadingSourceEntries()
    {
        var reads = new PlaylistSpeedReads();
        using var factory = ClientMixFactory(new(), configure: services =>
        {
            services.RemoveAll<IDbContextFactory<AllstarrDbContext>>();
            services.AddSingleton<IDbContextFactory<AllstarrDbContext>>(provider => new AdminAuthTestDbContextFactory(
                new DbContextOptionsBuilder<AllstarrDbContext>(provider.GetRequiredService<DbContextOptions<AllstarrDbContext>>()).AddInterceptors(reads).Options));
        }, nativeResponse: request => request.RequestUri!.AbsolutePath is "/Items" or "/Users/listener/Items"
            ? Json(200, """{"Items":[],"TotalRecordCount":0}""") : null);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var mix = await SeedClientMixAsync(factory);
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        var saved = await db.ExternalMetadataSnapshots.AsNoTracking().SingleAsync(item => item.ResourceKind == "personal-mixes");
        var document = JsonSerializer.Deserialize<PersonalMixDocument>(saved.PayloadJson)!;
        document.Mixes["hidden"] = mix with { Id = Guid.NewGuid(), Added = false };
        document.Mixes["unbuilt"] = mix with { Id = Guid.NewGuid(), Generation = 0 };
        saved.Id = Guid.NewGuid();
        saved.SnapshotVersion++;
        saved.PayloadJson = JsonSerializer.Serialize(document);
        db.ExternalMetadataSnapshots.Add(saved);
        var owner = saved.OwnerUserId;
        var link = new PlaylistLinkRecord
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            SourceProviderId = "lastfm",
            SourcePlaylistId = "saved",
            SourcePlaylistIdHash = new('e', 64),
            TargetProtocol = "jellyfin",
            TargetBackendInstanceId = "primary",
            Mode = PlaylistLinkMode.Virtual,
            Enabled = true
        };
        db.PlaylistLinks.Add(link);
        db.PlaylistSourceSnapshots.Add(new()
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            PlaylistLinkId = link.Id,
            SnapshotVersion = 1,
            ProviderRevision = "1",
            Name = "Saved",
            PayloadSha256 = new('f', 64),
            PublishedAt = DateTimeOffset.UtcNow
        });
        var account = new ProviderAccountRecord { Id = Guid.NewGuid(), OwnerUserId = owner, ProviderId = "lastfm", Enabled = true };
        db.ProviderAccounts.Add(account);
        var feed = new PlaylistLinkRecord
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            ProviderAccountId = account.Id,
            SourceProviderId = "lastfm",
            SourcePlaylistId = "loved",
            SourcePlaylistIdHash = new('d', 64),
            TargetProtocol = "jellyfin",
            TargetBackendInstanceId = "primary",
            Mode = PlaylistLinkMode.Virtual,
            Enabled = true
        };
        db.PlaylistLinks.Add(feed);
        db.PlaylistSourceSnapshots.Add(new()
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            PlaylistLinkId = feed.Id,
            ProviderAccountId = account.Id,
            SnapshotVersion = 1,
            ProviderRevision = "1",
            Name = "Loved tracks",
            PayloadSha256 = new('c', 64),
            PublishedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        const string countPath = "/Items?userId=listener&limit=0&recursive=true&includeItemTypes=Playlist&enableTotalRecordCount=true";
        reads.Commands.Clear();
        reads.SavedMixReads = reads.ForkReads = reads.FavoriteReads = 0;
        using var count = await client.GetAsync(countPath);
        count.EnsureSuccessStatusCode();
        using var counted = JsonDocument.Parse(await count.Content.ReadAsStringAsync());
        Assert.Empty(counted.RootElement.GetProperty("Items").EnumerateArray());
        Assert.Equal(3, counted.RootElement.GetProperty("TotalRecordCount").GetInt32());
        Assert.DoesNotContain(reads.Commands, command => command.Contains("playlist_source_entries", StringComparison.Ordinal));
        using var browse = await client.GetAsync(countPath.Replace("limit=0", "limit=50", StringComparison.Ordinal));
        browse.EnsureSuccessStatusCode();
        using var listed = JsonDocument.Parse(await browse.Content.ReadAsStringAsync());
        Assert.Equal(listed.RootElement.GetProperty("TotalRecordCount").GetInt32(), counted.RootElement.GetProperty("TotalRecordCount").GetInt32());
        account.Enabled = false;
        await db.SaveChangesAsync();
        using var disabled = await client.GetAsync(countPath);
        using var withoutFeed = JsonDocument.Parse(await disabled.Content.ReadAsStringAsync());
        Assert.Equal(2, withoutFeed.RootElement.GetProperty("TotalRecordCount").GetInt32());
        link.Enabled = false;
        await db.SaveChangesAsync();
        using var removed = await client.GetAsync(countPath);
        using var updated = JsonDocument.Parse(await removed.Content.ReadAsStringAsync());
        Assert.Equal(1, updated.RootElement.GetProperty("TotalRecordCount").GetInt32());
        using var filtered = await client.GetAsync(countPath + "&SearchTerm=missing");
        using var matching = JsonDocument.Parse(await filtered.Content.ReadAsStringAsync());
        Assert.Equal(0, matching.RootElement.GetProperty("TotalRecordCount").GetInt32());
    }

    private sealed class PlaylistSpeedReads : DbCommandInterceptor
    {
        internal ConcurrentQueue<string> Commands { get; } = new();
        internal int SavedMixReads, ForkReads, FavoriteReads;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Enqueue(command.CommandText);
            if (command.CommandText.Contains("external_metadata_snapshots", StringComparison.Ordinal) &&
                (command.CommandText.Contains("'personal-mixes'", StringComparison.Ordinal) ||
                 command.Parameters.Cast<DbParameter>().Any(parameter => parameter.Value is "personal-mixes")))
                Interlocked.Increment(ref SavedMixReads);
            if (command.CommandText.Contains("external_metadata_snapshots", StringComparison.Ordinal) &&
                (command.CommandText.Contains("'playlist-fork'", StringComparison.Ordinal) ||
                 command.Parameters.Cast<DbParameter>().Any(parameter => parameter.Value is "playlist-fork")))
                Interlocked.Increment(ref ForkReads);
            if (command.CommandText.Contains("favorite_states", StringComparison.Ordinal)) Interlocked.Increment(ref FavoriteReads);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
