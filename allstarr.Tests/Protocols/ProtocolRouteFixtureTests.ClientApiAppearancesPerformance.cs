using System.Data.Common;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.ClientApi;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
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
    public async Task ClientApi_AppearanceMembershipBatchMatchesSingletonsAndPreservesInputPositions()
    {
        var reads = new AppearanceReads();
        using var factory = AppearanceFactory(new(), reads);
        using var client = CreateClientApiClient(factory);
        var first = await SeedClientDetailsAsync(factory, "listener", "appearance-first", personal: false);
        var second = await SeedClientDetailsAsync(factory, "listener", "appearance-second", personal: false);
        var owner = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!.UserId;
        var other = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "other")))!.UserId;
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>();
        await using (var db = await contexts.CreateDbContextAsync())
        {
            var source = await db.ExternalMetadataSnapshots.SingleAsync(row => row.OwnerUserId == owner && row.ResourceKind == "track" &&
                row.ExternalIdHash == CanonicalCatalogKeys.Hash("appearance-first"));
            var secondSource = await db.ExternalMetadataSnapshots.SingleAsync(row => row.OwnerUserId == owner && row.ResourceKind == "track" &&
                row.ExternalIdHash == CanonicalCatalogKeys.Hash("appearance-second"));
            var ownAccount = Guid.NewGuid();
            var otherAccount = Guid.NewGuid();
            db.ProviderAccounts.AddRange(
                new() { Id = ownAccount, OwnerUserId = owner, ProviderId = "spotify", Enabled = true },
                new() { Id = otherAccount, OwnerUserId = other, ProviderId = "spotify", Enabled = true });
            AppearanceIdentity(db, first, "apple-musickit", "pinned-alias").Verification = ProviderIdentityVerification.Pinned;
            AppearanceIdentity(db, first, "spotify", "private-song", ownAccount);
            AppearanceIdentity(db, second, "spotify", "foreign-song", otherAccount);
            var privateSong = AppearanceSource(db, owner, "private-song", "spotify", ownAccount);
            var foreignSong = AppearanceSource(db, owner, "foreign-song", "spotify", otherAccount);
            var local = new LibraryTrackRecord { Id = Guid.NewGuid(), OwnerUserId = owner, Protocol = "jellyfin", BackendInstanceId = "primary", BackendItemId = "batch-local", CanonicalRecordingId = first };
            var unmatched = new LibraryTrackRecord { Id = Guid.NewGuid(), OwnerUserId = owner, Protocol = "jellyfin", BackendInstanceId = "primary", BackendItemId = "batch-track-only" };
            var wrongBackend = new LibraryTrackRecord { Id = Guid.NewGuid(), OwnerUserId = owner, Protocol = "jellyfin", BackendInstanceId = "other", BackendItemId = "batch-track-only" };
            db.LibraryTracks.AddRange(local, unmatched, wrongBackend);
            AppearancePlaylist(db, owner, "Alpha", [(source, null), (source, null), (secondSource, null)]);
            AppearancePlaylist(db, owner, "Private route", [(privateSong, null)]);
            AppearancePlaylist(db, owner, "Foreign route", [(foreignSong, null)]);
            AppearancePlaylist(db, other, "Other owner", [(AppearanceSource(db, other, "appearance-first", "deezer"), null)]);
            var accepted = AppearanceSource(db, owner, "accepted");
            var pinned = AppearanceSource(db, owner, "pinned");
            var rejected = AppearanceSource(db, owner, "rejected");
            var trackOnly = AppearanceSource(db, owner, "track-only");
            var wrong = AppearanceSource(db, owner, "wrong-backend");
            AppearancePlaylist(db, owner, "Accepted", [(accepted, AppearanceMatch(db, accepted, first, null, TrackMatchState.Accepted))]);
            AppearancePlaylist(db, owner, "Pinned", [(pinned, AppearanceMatch(db, pinned, null, local.Id, TrackMatchState.Pinned))]);
            AppearancePlaylist(db, owner, "Rejected", [(rejected, AppearanceMatch(db, rejected, first, null, TrackMatchState.Rejected))]);
            AppearancePlaylist(db, owner, "Track only", [(trackOnly, AppearanceMatch(db, trackOnly, null, unmatched.Id, TrackMatchState.Accepted))]);
            AppearancePlaylist(db, owner, "Wrong backend", [(wrong, AppearanceMatch(db, wrong, null, wrongBackend.Id, TrackMatchState.Accepted))]);
            var replaced = AppearancePlaylist(db, owner, "Old membership", [(source, null)]);
            AppearanceSnapshot(db, replaced, "Replacement", [(secondSource, null)], 2);
            var pending = AppearancePlaylist(db, owner, "Published membership", [(source, null)]);
            AppearanceSnapshot(db, pending, "Unpublished", [(secondSource, null)], 2).PublishedAt = null;
            await db.SaveChangesAsync();
        }
        SongPlaylistIdentity[] inputs = [
            new(CanonicalRecordingId: first), new(ProviderId: "apple-download", ExternalId: " pinned-alias "),
            new(Protocol: " Jellyfin ", BackendInstanceId: " primary ", BackendItemId: "batch-local"),
            new(CanonicalRecordingId: second), new(ProviderId: "deezer", ExternalId: "appearance-second"),
            new(Protocol: "jellyfin", BackendInstanceId: "primary", BackendItemId: "batch-track-only"),
            new(Protocol: "jellyfin", BackendInstanceId: "missing", BackendItemId: "batch-track-only"),
            new(CanonicalRecordingId: first), new(ProviderId: "deezer", ExternalId: CanonicalCatalogKeys.Hash("appearance-first")),
            new(second, "apple-download", "pinned-alias", "jellyfin", "primary", "batch-track-only")];
        var reader = new SongPlaylistMembershipReader(contexts);
        var expected = new List<IReadOnlyList<SongPlaylistMembership>>();
        foreach (var input in inputs) expected.Add(await reader.ReadAsync(owner, input));
        reads.Count = 0;
        var actual = await reader.ReadAsync(inputs, owner);
        Assert.InRange(reads.Count, 1, 3);
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < inputs.Length; index++)
        {
            Assert.Equal(expected[index], actual[index]);
            Assert.Equal(actual[index].Count, actual[index].Select(row => row.Id).Distinct().Count());
        }
        Assert.Equal(["Accepted", "Alpha", "Pinned", "Private route", "Published membership"], actual[0].Select(row => row.Name));
        Assert.Equal("Track only", Assert.Single(actual[5]).Name);
        Assert.Empty(actual[6]);
        Assert.DoesNotContain(actual[3], row => row.Name == "Foreign route");
        reads.Count = 0;
        var repeated = await reader.ReadAsync(Enumerable.Range(0, 32).SelectMany(_ => inputs).ToArray(), owner);
        Assert.InRange(reads.Count, 1, 3);
        for (var index = 0; index < repeated.Count; index++) Assert.Equal(expected[index % inputs.Length], repeated[index]);
        Assert.Empty(await reader.ReadAsync(Array.Empty<SongPlaylistIdentity>(), owner));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadAsync(new SongPlaylistIdentity[] { new(BackendItemId: "incomplete") }, owner));
    }

    [Fact]
    public async Task ClientApi_AlbumAppearancesUseBoundedReadsAndKeepDuplicateSongCountsAndCurrentPublication()
    {
        var reads = new AppearanceReads();
        var songs = Enumerable.Range(0, 31).Select(index => new Song
        {
            Id = $"ext-deezer-song-appearance-{index}",
            ExternalProvider = "deezer",
            ExternalId = $"appearance-{index}",
            Title = $"Song {index}"
        }).ToArray();
        var album = new Album { Id = "ext-deezer-album-appearance-batch", Songs = [songs[0]] };
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(service => service.GetAlbumAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "appearance-batch")).ReturnsAsync(() => album);
        using var factory = AppearanceFactory(gateway, reads);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var owner = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!.UserId;
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>();
        PlaylistLinkRecord alpha;
        PlaylistLinkRecord beta;
        await using (var db = await contexts.CreateDbContextAsync())
        {
            var sources = songs.Select(song => AppearanceSource(db, owner, song.ExternalId!, "deezer")).ToArray();
            alpha = AppearancePlaylist(db, owner, "Alpha", sources.Select(source => (source, (Guid?)null)).Append((sources[0], null)).ToArray());
            beta = AppearancePlaylist(db, owner, "Beta", [(sources[0], null)]);
            await db.SaveChangesAsync();
        }
        const string itemId = "ext-deezer-album-appearance-batch";
        using (var small = await client.GetAsync($"/allstarr/v1/items/{itemId}/appearances")) small.EnsureSuccessStatusCode();
        reads.Count = 0;
        using (var small = await client.GetAsync($"/allstarr/v1/items/{itemId}/appearances")) small.EnsureSuccessStatusCode();
        var smallReads = reads.Count;
        album.Songs = [.. songs, songs[0]];
        reads.Count = 0;
        using var large = await client.GetAsync($"/allstarr/v1/items/{itemId}/appearances");
        large.EnsureSuccessStatusCode();
        var largeReads = reads.Count;
        Assert.True(largeReads <= smallReads + 3, $"1 song used {smallReads} reads; 32 songs used {largeReads} reads.");
        ClientItemAppearance Card(PlaylistLinkRecord link, string title, int count)
        {
            var id = PlaylistVirtualizationService.CreateProtocolId(link.Id);
            return new("playlist", id, title, "From Deezer", $"/Items/{Uri.EscapeDataString(id)}/Images/Primary", count, id);
        }
        Assert.Equal(JsonSerializer.Serialize(new ClientItemAppearances(itemId, 32, [Card(alpha, "Alpha", 32), Card(beta, "Beta", 2)])),
            await large.Content.ReadAsStringAsync());
        await using (var db = await contexts.CreateDbContextAsync())
        {
            AppearanceSnapshot(db, alpha, "Removed songs", [], 2);
            await db.SaveChangesAsync();
        }
        using var refreshed = await client.GetAsync($"/allstarr/v1/items/{itemId}/appearances");
        refreshed.EnsureSuccessStatusCode();
        Assert.Equal(JsonSerializer.Serialize(new ClientItemAppearances(itemId, 32, [Card(beta, "Beta", 2)])),
            await refreshed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ClientApi_AppearancesReuseMixResolutionAndInvalidateOnSavedChanges()
    {
        var reads = new AppearanceReads();
        using var factory = AppearanceFactory(new(), reads);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var mix = await SeedClientMixAsync(factory);
        const string path = "/allstarr/v1/items/native-mix-song/appearances";
        using var cold = await client.GetAsync(path);
        cold.EnsureSuccessStatusCode();
        AssertTimingStages(cold, "merge", "match");
        var expected = await cold.Content.ReadAsStringAsync();
        reads.Count = 0;
        using var warm = await client.GetAsync(path);
        warm.EnsureSuccessStatusCode();
        Assert.Equal(expected, await warm.Content.ReadAsStringAsync());
        AssertTimingStages(warm, "match");
        _lyricsSearchOutput.WriteLine($"Appearances repeat: {reads.Count} database reads.");
        Assert.DoesNotContain(", merge;dur=", Assert.Single(warm.Headers.GetValues("Server-Timing")));
        foreach (var concurrent in await ReadConcurrentTimingAsync(client, path))
            Assert.Equal(expected, System.Text.Encoding.UTF8.GetString(concurrent));
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            var row = await db.ExternalMetadataSnapshots.SingleAsync(item => item.ResourceKind == "personal-mixes");
            var next = JsonSerializer.Deserialize<ExternalMetadataSnapshotRecord>(JsonSerializer.Serialize(row))!;
            next.Id = Guid.NewGuid();
            next.PayloadJson = row.PayloadJson.Replace("Saved favourites", "Renamed favourites", StringComparison.Ordinal);
            next.PayloadSha256 = CanonicalCatalogKeys.Hash(next.PayloadJson);
            next.SnapshotVersion++;
            next.RetrievedAt = DateTimeOffset.UtcNow;
            db.ExternalMetadataSnapshots.Add(next);
            await db.SaveChangesAsync();
        }
        using var changed = await client.GetAsync(path);
        changed.EnsureSuccessStatusCode();
        Assert.Contains("Renamed favourites", await changed.Content.ReadAsStringAsync());
        AssertTimingStages(changed, "merge");
    }

    [Fact]
    public async Task ClientApi_AppearancesBatchPreparedFeedsAndPreserveCards()
    {
        var reads = new AppearanceReads();
        using var factory = AppearanceFactory(new(), reads);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var mix = await SeedClientMixAsync(factory);
        await SeedAppearanceFeedsAsync(factory, 1);
        const string path = "/allstarr/v1/items/native-mix-song/appearances";
        reads.Count = 0;
        using (var small = await client.GetAsync(path)) small.EnsureSuccessStatusCode();
        var smallReads = reads.Count;
        var feeds = await SeedAppearanceFeedsAsync(factory, 12);
        reads.Count = 0;
        using var large = await client.GetAsync(path);
        large.EnsureSuccessStatusCode();
        Assert.True(reads.Count <= smallReads + 3, $"1 feed used {smallReads} reads; 12 feeds used {reads.Count} reads.");
        var expected = feeds.Select(feed => new ClientItemAppearance("mix", feed.Id.ToString("D"), feed.Name, "Spotify",
            $"/allstarr/v1/mixes/{feed.Id}/cover", 2, null)).Append(new("mix", mix.Id.ToString("D"), mix.Name,
            "Made for you", $"/allstarr/v1/mixes/{mix.Id}/cover", 1, PlaylistVirtualizationService.CreateProtocolId(mix.Id))).ToArray();
        Assert.Equal(JsonSerializer.Serialize(new ClientItemAppearances("native-mix-song", 1, expected)), await large.Content.ReadAsStringAsync());
        reads.Count = 0;
        using var warm = await client.GetAsync(path);
        warm.EnsureSuccessStatusCode();
        Assert.Equal(await large.Content.ReadAsStringAsync(), await warm.Content.ReadAsStringAsync());
        Assert.DoesNotContain(", merge;dur=", Assert.Single(warm.Headers.GetValues("Server-Timing")));
    }

    [Fact(Skip = "Manual fixture measurement; run on each revision to compare cold and warm request timing.")]
    public async Task ClientApi_AppearancesReportPreparedFeedRequestTiming()
    {
        var reads = new AppearanceReads();
        using var factory = AppearanceFactory(new(), reads);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        await SeedClientMixAsync(factory);
        await SeedAppearanceFeedsAsync(factory, 12);
        foreach (var state in new[] { "cold", "warm" })
        {
            reads.Count = 0;
            using var response = await client.GetAsync("/allstarr/v1/items/native-mix-song/appearances");
            response.EnsureSuccessStatusCode();
            _lyricsSearchOutput.WriteLine($"Appearances {state}: {reads.Count} database reads; " + Assert.Single(response.Headers.GetValues("Server-Timing")));
        }
    }

    private static async Task<PersonalMix[]> SeedAppearanceFeedsAsync(ProtocolFactory factory, int count)
    {
        var owner = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!.UserId;
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        var user = await db.Users.SingleAsync(item => item.Id == owner);
        var account = await db.ProviderAccounts.SingleOrDefaultAsync(item => item.OwnerUserId == owner && item.ProviderId == "spotify");
        if (account == null)
        {
            account = new() { Id = Guid.NewGuid(), OwnerUserId = owner, ProviderId = "spotify", Enabled = true, Revision = 1 };
            db.ProviderAccounts.Add(account);
        }
        var scope = CanonicalCatalogKeys.Hash($"{user.Id}:{user.BackendType}:{user.BackendInstanceId}:{user.BackendPrincipalId}");
        var version = (await db.ExternalMetadataSnapshots.Where(item => item.OwnerUserId == owner && item.ResourceKind == "service-feeds")
            .Select(item => (int?)item.SnapshotVersion).MaxAsync() ?? 0) + 1;
        var cards = Enumerable.Range(0, count).Select(index => new PersonalMix(Guid.NewGuid(), "external", $"Feed {index:00}", "From Spotify", 1,
            null, new(true, "Ready"), false, [])
        { Source = "spotify", SourceLabel = "Spotify", SourcePlaylistId = $"appearance-feed-{index}", ProviderAccountId = account.Id }).ToArray();
        void Snapshot(string kind, string key, object payload)
        {
            var json = JsonSerializer.Serialize(payload);
            db.ExternalMetadataSnapshots.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                ProviderAccountId = account.Id,
                ProviderId = "spotify",
                ResourceKind = kind,
                ExternalIdHash = key,
                SnapshotVersion = version,
                Protocol = user.BackendType,
                BackendInstanceId = user.BackendInstanceId,
                BackendPrincipalId = user.BackendPrincipalId,
                ProviderRevision = "1:artwork-v1",
                PayloadJson = json,
                PayloadSha256 = CanonicalCatalogKeys.Hash(json),
                RetrievedAt = DateTimeOffset.UtcNow
            });
        }
        Snapshot("service-feeds", CanonicalCatalogKeys.Hash(scope + ":1"), cards);
        foreach (var card in cards)
        {
            var song = new Song { Id = "native-mix-song", IsLocal = true, Title = "Library song", Artist = "Library artist" };
            var hidden = new Song { Id = "inaccessible-song", IsLocal = true, Title = "Hidden song" };
            Snapshot("service-feed-prepared", CanonicalCatalogKeys.Hash(scope + ":1:" + card.SourcePlaylistId), card with
            { Songs = [new(card.Id, 1, song, "library", "Saved", 1), new(card.Id, 1, song, "library", "Duplicate", 1), new(card.Id, 1, hidden, "library", "Hidden", 1)] });
        }
        await db.SaveChangesAsync();
        return cards;
    }

    private static ProtocolFactory AppearanceFactory(Mock<IProtocolProviderGateway> gateway, AppearanceReads reads) => ClientMixFactory(gateway, configure: services =>
    {
        services.RemoveAll<IDbContextFactory<AllstarrDbContext>>();
        services.AddSingleton<IDbContextFactory<AllstarrDbContext>>(provider => new AdminAuthTestDbContextFactory(
            new DbContextOptionsBuilder<AllstarrDbContext>(provider.GetRequiredService<DbContextOptions<AllstarrDbContext>>()).AddInterceptors(reads).Options));
    });

    private static ProviderTrackIdentityRecord AppearanceIdentity(AllstarrDbContext db, Guid recording, string provider, string external, Guid? account = null)
    {
        var identity = new ProviderTrackIdentityRecord
        {
            Id = Guid.NewGuid(),
            CanonicalRecordingId = recording,
            ProviderId = provider,
            ExternalId = external,
            ExternalIdHash = CanonicalCatalogKeys.Hash(external),
            ProviderAccountId = account,
            ResourceKind = ProviderResourceKind.Track,
            Scope = account.HasValue ? ProviderIdentityScope.Account : ProviderIdentityScope.Catalog,
            Verification = ProviderIdentityVerification.Verified,
            VerificationMethod = "fixture",
            DecisionVersion = 1
        };
        db.ProviderTrackIdentities.Add(identity);
        return identity;
    }

    private static ExternalMetadataSnapshotRecord AppearanceSource(AllstarrDbContext db, Guid owner, string external, string provider = "spotify", Guid? account = null)
    {
        var source = new ExternalMetadataSnapshotRecord
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            ProviderAccountId = account,
            ProviderId = provider,
            ResourceKind = "track",
            ExternalIdHash = CanonicalCatalogKeys.Hash(external),
            SnapshotVersion = 1,
            PayloadSha256 = CanonicalCatalogKeys.Hash("{}"),
            Protocol = "jellyfin",
            BackendInstanceId = "primary",
            BackendPrincipalId = "listener"
        };
        db.ExternalMetadataSnapshots.Add(source);
        return source;
    }

    private static Guid AppearanceMatch(AllstarrDbContext db, ExternalMetadataSnapshotRecord source, Guid? recording, Guid? track, TrackMatchState state)
    {
        var match = new TrackMatchRecord
        {
            Id = Guid.NewGuid(),
            OwnerUserId = source.OwnerUserId,
            ExternalSnapshotId = source.Id,
            CanonicalRecordingId = recording,
            LibraryTrackId = track,
            State = state,
            DecisionVersion = 1,
            SourceSnapshotVersion = 1
        };
        db.TrackMatches.Add(match);
        return match.Id;
    }

    private static PlaylistLinkRecord AppearancePlaylist(AllstarrDbContext db, Guid owner, string name, IReadOnlyList<(ExternalMetadataSnapshotRecord Source, Guid? Match)> entries)
    {
        var link = new PlaylistLinkRecord
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            SourceProviderId = "deezer",
            SourcePlaylistId = Guid.NewGuid().ToString(),
            TargetProtocol = "jellyfin",
            TargetBackendInstanceId = "primary",
            Mode = PlaylistLinkMode.Virtual,
            Enabled = true
        };
        link.SourcePlaylistIdHash = CanonicalCatalogKeys.Hash(link.SourcePlaylistId);
        db.PlaylistLinks.Add(link);
        AppearanceSnapshot(db, link, name, entries, 1);
        return link;
    }

    private static PlaylistSourceSnapshotRecord AppearanceSnapshot(AllstarrDbContext db, PlaylistLinkRecord link, string name,
        IReadOnlyList<(ExternalMetadataSnapshotRecord Source, Guid? Match)> entries, int version)
    {
        var snapshot = new PlaylistSourceSnapshotRecord
        {
            Id = Guid.NewGuid(),
            OwnerUserId = link.OwnerUserId,
            PlaylistLinkId = link.Id,
            Name = name,
            SnapshotVersion = version,
            PublishedAt = DateTimeOffset.UtcNow,
            PayloadSha256 = CanonicalCatalogKeys.Hash(name)
        };
        db.PlaylistSourceSnapshots.Add(snapshot);
        for (var position = 0; position < entries.Count; position++) db.PlaylistSourceEntries.Add(new()
        {
            Id = Guid.NewGuid(),
            PlaylistSourceSnapshotId = snapshot.Id,
            ExternalMetadataSnapshotId = entries[position].Source.Id,
            PublishedTrackMatchId = entries[position].Match,
            SourcePosition = position,
            SourceEntryIdHash = CanonicalCatalogKeys.Hash($"{snapshot.Id}:{position}")
        });
        return snapshot;
    }

    private sealed class AppearanceReads : DbCommandInterceptor
    {
        internal int Count;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Count);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
