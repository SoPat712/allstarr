using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed class TrackMatchListPresentationTests
{
    [Fact]
    [Trait("Category", "Sqlite")]
    public async Task ListSupportsNeedsStatePlaylistNamesSelectionSearchAndSorts()
    {
        await using var fixture = await Fixture.CreateAsync();
        var controller = fixture.Controller();
        var needs = Json(await controller.List(state: "needs"));
        Assert.Equal(3, needs.GetProperty("pagination").GetProperty("total").GetInt32());
        var byTitle = Json(await controller.List(state: "", sort: "title", pageSize: 10));
        var titles = byTitle.GetProperty("matches").EnumerateArray().Select(item => item.GetProperty("title").GetString()).ToArray();
        Assert.Equal(["Older title", "Retained title", "Suggested title"], titles.Take(3));
        var byPlaylist = Json(await controller.List(sort: "playlist", pageSize: 10));
        var first = byPlaylist.GetProperty("matches").EnumerateArray().First();
        Assert.Equal(["Road mix"], first.GetProperty("playlistNames").EnumerateArray().Select(item => item.GetString()!).ToArray());
        var playlistSearch = Json(await controller.List(search: "road mix", pageSize: 10));
        Assert.Single(playlistSearch.GetProperty("matches").EnumerateArray());
        var selection = Json(await controller.Selection(state: "needs"));
        Assert.Equal(3, selection.GetProperty("matches").GetArrayLength());
        Assert.Contains(fixture.SuggestedId,
            selection.GetProperty("matches").EnumerateArray().Select(item => item.GetProperty("externalSnapshotId").GetGuid()));
        var row = selection.GetProperty("matches").EnumerateArray().First(item => item.GetProperty("externalSnapshotId").GetGuid() == fixture.SuggestedId);
        Assert.False(row.TryGetProperty("warnings", out _));
        var rejectedSort = await controller.List(sort: "unsupported");
        Assert.IsType<BadRequestObjectResult>(rejectedSort);
    }

    [Fact]
    [Trait("Category", "Sqlite")]
    public async Task PlaylistNamesIgnoreOtherListenersPlaylists()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var db = fixture.Factory.CreateDbContext();
        var foreign = Guid.CreateVersion7();
        db.Users.Add(new() { Id = foreign, DisplayName = "Other", Enabled = true });
        var link = Guid.CreateVersion7();
        var snapshot = Guid.CreateVersion7();
        db.PlaylistLinks.Add(new()
        {
            Id = link,
            OwnerUserId = foreign,
            SourceProviderId = "spotify",
            SourcePlaylistId = "other",
            SourcePlaylistIdHash = Hash("other"),
            TargetProtocol = "jellyfin",
            TargetBackendInstanceId = "fixture",
            Mode = PlaylistLinkMode.Virtual,
            CreatedAt = fixture.Now,
            UpdatedAt = fixture.Now
        });
        db.PlaylistSourceSnapshots.Add(new()
        {
            Id = snapshot,
            OwnerUserId = foreign,
            PlaylistLinkId = link,
            SnapshotVersion = 1,
            RetrievedAt = fixture.Now,
            PublishedAt = fixture.Now,
            Name = "Foreign",
            PayloadSha256 = Hash("foreign")
        });
        db.PlaylistSourceEntries.Add(new()
        {
            Id = Guid.CreateVersion7(),
            PlaylistSourceSnapshotId = snapshot,
            ExternalMetadataSnapshotId = fixture.RetainedId,
            SourcePosition = 0,
            SourceEntryIdHash = Hash("foreign-entry")
        });
        await db.SaveChangesAsync();
        var row = Assert.Single(Json(await fixture.Controller().List(externalSnapshotId: fixture.RetainedId)).GetProperty("matches").EnumerateArray());
        Assert.Equal(["Road mix"], row.GetProperty("playlistNames").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    [Trait("Category", "Sqlite")]
    public async Task PlaylistNameSearchKeepsTheNewestSnapshot()
    {
        await using var fixture = await Fixture.CreateAsync();
        var seed = await fixture.SeedRefreshAsync();
        var controller = fixture.Controller();
        var listed = Rows(await controller.List(pageSize: 20));
        Assert.DoesNotContain(listed, row => row.GetProperty("title").GetString() == "Old title");
        Assert.DoesNotContain(listed, row => row.GetProperty("externalSnapshotId").GetGuid() == seed.OlderId);
        var current = Assert.Single(listed, row => row.GetProperty("externalSnapshotId").GetGuid() == seed.NewerId);
        Assert.Equal("New title", current.GetProperty("title").GetString());
        Assert.Equal(["Night drive", "Dawn mix"], Names(current));

        var night = Rows(await controller.List(search: "night", pageSize: 20));
        var matched = Assert.Single(night, row => row.GetProperty("title").GetString() == "New title");
        Assert.Equal(seed.NewerId, matched.GetProperty("externalSnapshotId").GetGuid());
        Assert.DoesNotContain(night, row => row.GetProperty("externalSnapshotId").GetGuid() == seed.OlderId);
        Assert.Equal(["Night drive", "Dawn mix"], Names(matched));
        var selected = Rows(await controller.Selection(search: "night"));
        Assert.Contains(selected, row => row.GetProperty("externalSnapshotId").GetGuid() == seed.NewerId);
        Assert.DoesNotContain(selected, row => row.GetProperty("externalSnapshotId").GetGuid() == seed.OlderId);
    }

    [Fact]
    [Trait("Category", "Sqlite")]
    public async Task PlaylistNameSearchMatchesTheOpenView()
    {
        await using var fixture = await Fixture.CreateAsync();
        var seed = await fixture.SeedRefreshAsync();
        var controller = fixture.Controller();
        var night = Rows(await controller.List(search: "night", pageSize: 20))
            .Select(row => row.GetProperty("title").GetString()!).OrderBy(title => title, StringComparer.Ordinal).ToArray();
        Assert.Equal(["Album only", "New title", "Night call", "Provider only", "Skipped"], night);

        var rejected = Assert.Single(Rows(await controller.List(state: "manual_rejections", search: "night", pageSize: 20)));
        Assert.Equal(seed.SkippedId, rejected.GetProperty("externalSnapshotId").GetGuid());
        Assert.Equal("rejected", rejected.GetProperty("state").GetString());
        Assert.Empty(Rows(await controller.List(state: "manual_rejections", pageSize: 20)));
        var chosen = Assert.Single(Rows(await controller.Selection(state: "manual_rejections", search: "night")));
        Assert.Equal(seed.SkippedId, chosen.GetProperty("externalSnapshotId").GetGuid());
        Assert.Empty(Rows(await controller.Selection(state: "manual_rejections")));

        var payload = Assert.Single(Rows(await controller.List(search: "night1", pageSize: 20)));
        Assert.Equal(seed.QuietId, payload.GetProperty("externalSnapshotId").GetGuid());
        Assert.Equal("Quiet", payload.GetProperty("title").GetString());
    }

    [Fact]
    [Trait("Category", "Sqlite")]
    public async Task NewestSortPutsARowWithNoDecisionLast()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var db = fixture.Factory.CreateDbContext();
        var earlier = Fixture.Snapshot(fixture, "track", "earlier", "{\"Title\":\"Earlier title\",\"Artist\":\"Earlier artist\"}", fixture.Now.AddHours(-2));
        var undecided = Fixture.Snapshot(fixture, "track", "undecided", "{\"Title\":\"Undecided title\",\"Artist\":\"Undecided artist\"}", fixture.Now.AddMinutes(5));
        db.ExternalMetadataSnapshots.AddRange(earlier, undecided);
        var earlierRecording = Recording(fixture.Owner, "Earlier title", fixture.Now);
        db.CanonicalRecordings.Add(earlierRecording);
        db.TrackMatches.Add(new()
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = fixture.Owner,
            ExternalSnapshotId = earlier.Id,
            CanonicalRecordingId = earlierRecording.Id,
            State = TrackMatchState.Suggested,
            Confidence = .6,
            DecisionVersion = 1,
            SourceSnapshotVersion = earlier.SnapshotVersion,
            Revision = 1,
            DecidedAt = fixture.Now.AddHours(-2)
        });
        await db.SaveChangesAsync();
        var matches = Rows(await fixture.Controller().List(sort: "newest", pageSize: 20));
        var titles = matches.Select(row => row.GetProperty("title").GetString()).ToArray();
        Assert.Equal(["Suggested title", "Earlier title"], titles.Take(2));
        Assert.Contains("Undecided title", titles);
        Assert.All(matches.Skip(2), row => Assert.Equal(JsonValueKind.Null, row.GetProperty("decidedAt").ValueKind));
    }

    private static JsonElement Json(IActionResult result) =>
        JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static JsonElement[] Rows(IActionResult result) =>
        Json(result).GetProperty("matches").EnumerateArray().ToArray();

    private static string[] Names(JsonElement row) =>
        row.GetProperty("playlistNames").EnumerateArray().Select(item => item.GetString()!).ToArray();

    private sealed record Refresh(Guid OlderId, Guid NewerId, Guid QuietId, Guid CalledId, Guid AlbumId, Guid ProviderSongId, Guid SkippedId);

    private sealed class Fixture(SqliteTestDatabase database) : IAsyncDisposable
    {
        public Factory Factory { get; } = new(database.Options);
        public Guid Owner { get; } = Guid.CreateVersion7();
        public DateTimeOffset Now { get; } = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        public Guid RetainedId { get; private set; }
        public Guid SuggestedId { get; private set; }

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture(await SqliteTestDatabase.CreateAsync());
            await using var db = fixture.Factory.CreateDbContext();
            db.Users.Add(new() { Id = fixture.Owner, BackendType = "jellyfin", BackendInstanceId = "fixture", BackendPrincipalId = "listener", DisplayName = "Listener", Enabled = true });
            var retained = Snapshot(fixture, "track", "retained", "{\"Title\":\"Retained title\",\"Artist\":\"Retained artist\"}", at: fixture.Now);
            var suggested = Snapshot(fixture, "track", "suggested", "{\"Title\":\"Suggested title\",\"Artist\":\"Suggested artist\"}", at: fixture.Now.AddMinutes(-2));
            var older = Snapshot(fixture, "track", "older", "{\"Title\":\"Older title\",\"Artist\":\"Older artist\"}", at: fixture.Now.AddMinutes(-1));
            fixture.RetainedId = retained.Id;
            fixture.SuggestedId = suggested.Id;
            db.ExternalMetadataSnapshots.AddRange(retained, suggested, older);
            var suggestedRecording = Recording(fixture.Owner, "Suggested title", fixture.Now);
            db.CanonicalRecordings.Add(suggestedRecording);
            db.TrackMatches.Add(new()
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = fixture.Owner,
                ExternalSnapshotId = suggested.Id,
                CanonicalRecordingId = suggestedRecording.Id,
                State = TrackMatchState.Suggested,
                Confidence = .6,
                DecisionVersion = 1,
                SourceSnapshotVersion = suggested.SnapshotVersion,
                Revision = 1,
                DecidedAt = fixture.Now
            });
            var link = Guid.CreateVersion7();
            var playlistSnapshot = Guid.CreateVersion7();
            db.PlaylistLinks.Add(new()
            {
                Id = link,
                OwnerUserId = fixture.Owner,
                SourceProviderId = "spotify",
                SourcePlaylistId = "playlist-a",
                SourcePlaylistIdHash = Hash("playlist-a"),
                TargetProtocol = "jellyfin",
                TargetBackendInstanceId = "fixture",
                Mode = PlaylistLinkMode.Virtual,
                CreatedAt = fixture.Now,
                UpdatedAt = fixture.Now
            });
            db.PlaylistSourceSnapshots.Add(new()
            {
                Id = playlistSnapshot,
                OwnerUserId = fixture.Owner,
                PlaylistLinkId = link,
                SnapshotVersion = 1,
                RetrievedAt = fixture.Now,
                PublishedAt = fixture.Now,
                Name = "Road mix",
                PayloadSha256 = Hash("playlist-a")
            });
            db.PlaylistSourceEntries.Add(new()
            {
                Id = Guid.CreateVersion7(),
                PlaylistSourceSnapshotId = playlistSnapshot,
                ExternalMetadataSnapshotId = retained.Id,
                SourcePosition = 0,
                SourceEntryIdHash = Hash("retained-entry")
            });
            await db.SaveChangesAsync();
            return fixture;
        }

        public async Task<Refresh> SeedRefreshAsync()
        {
            await using var db = Factory.CreateDbContext();
            var older = Snapshot(this, "track", "refreshed", "{\"Title\":\"Old title\",\"Artist\":\"Old artist\"}", Now.AddDays(-2), 1);
            var newer = Snapshot(this, "track", "refreshed", "{\"Title\":\"New title\",\"Artist\":\"New artist\"}", Now, 2);
            var quiet = Snapshot(this, "track", "quiet", "{\"Title\":\"Quiet\",\"Artist\":\"Someone\",\"Album\":\"Notes\",\"isrc\":\"NIGHT1\",\"genre\":\"Night\"}");
            var called = Snapshot(this, "track", "called", "{\"Title\":\"Night call\",\"Artist\":\"Someone\"}");
            var album = Snapshot(this, "track", "album", "{\"Title\":\"Album only\",\"Artist\":\"Someone\",\"Album\":\"Night notes\"}");
            var provider = Snapshot(this, "track", "provider", "{\"Title\":\"Provider only\",\"Artist\":\"Someone\"}");
            provider.ProviderId = "nightfm";
            var skipped = Snapshot(this, "track", "skipped", "{\"Title\":\"Skipped\",\"Artist\":\"Someone\"}");
            db.ExternalMetadataSnapshots.AddRange(older, newer, quiet, called, album, provider, skipped);
            db.TrackMatches.Add(new()
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = Owner,
                ExternalSnapshotId = skipped.Id,
                State = TrackMatchState.Rejected,
                Confidence = 0,
                DecisionVersion = 1,
                SourceSnapshotVersion = skipped.SnapshotVersion,
                Revision = 1,
                DecidedAt = Now
            });
            AddPlaylist("night-drive", "Night drive", Now.AddHours(-3), older.Id, skipped.Id);
            AddPlaylist("dawn-mix", "Dawn mix", Now.AddHours(-1), newer.Id);
            await db.SaveChangesAsync();
            return new(older.Id, newer.Id, quiet.Id, called.Id, album.Id, provider.Id, skipped.Id);

            void AddPlaylist(string key, string name, DateTimeOffset created, params Guid[] songs)
            {
                var link = Guid.CreateVersion7();
                var snapshotId = Guid.CreateVersion7();
                db.PlaylistLinks.Add(new()
                {
                    Id = link,
                    OwnerUserId = Owner,
                    SourceProviderId = "spotify",
                    SourcePlaylistId = key,
                    SourcePlaylistIdHash = Hash(key),
                    TargetProtocol = "jellyfin",
                    TargetBackendInstanceId = "fixture",
                    Mode = PlaylistLinkMode.Virtual,
                    CreatedAt = created,
                    UpdatedAt = created
                });
                db.PlaylistSourceSnapshots.Add(new()
                {
                    Id = snapshotId,
                    OwnerUserId = Owner,
                    PlaylistLinkId = link,
                    SnapshotVersion = 1,
                    RetrievedAt = created,
                    PublishedAt = created,
                    Name = name,
                    PayloadSha256 = Hash(name)
                });
                for (var index = 0; index < songs.Length; index++)
                    db.PlaylistSourceEntries.Add(new()
                    {
                        Id = Guid.CreateVersion7(),
                        PlaylistSourceSnapshotId = snapshotId,
                        ExternalMetadataSnapshotId = songs[index],
                        SourcePosition = index,
                        SourceEntryIdHash = Hash(key + ":" + index)
                    });
            }
        }

        public static ExternalMetadataSnapshotRecord Snapshot(Fixture fixture, string kind, string id, string payload, DateTimeOffset? at = null, int version = 1) => new()
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = fixture.Owner,
            ProviderId = "spotify",
            ResourceKind = kind,
            ExternalIdHash = Hash(id),
            PayloadJson = payload,
            PayloadSha256 = Hash(payload),
            SnapshotVersion = version,
            ProviderRevision = "fixture",
            BackendInstanceId = "fixture",
            BackendPrincipalId = "listener",
            Protocol = "jellyfin",
            RetrievedAt = at ?? fixture.Now
        };

        public TrackMatchesController Controller()
        {
            var gateway = new Mock<IProtocolProviderGateway>();
            gateway.Setup(item => item.GetPlayableProviderOrderAsync(It.IsAny<ProviderActorContext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<string>());
            var http = new DefaultHttpContext();
            http.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
            {
                SessionId = "fixture",
                UserId = "listener",
                UserName = "Listener",
                IsAdministrator = false,
                AllstarrUserId = Owner,
                BackendType = "jellyfin",
                BackendInstanceId = "fixture",
                JellyfinAccessToken = "fixture",
                ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
            };
            var service = new TrackMatchCommandService(Factory, new TrackMatchDecisionEngine(), new ProviderAccountResolver(Factory),
                new SystemPlatformClock(), new TestBackendLibraryAccess(Factory, "music"));
            return new(service, gateway.Object, new AdminProtocolExecutionContextFactory(Factory, new SystemPlatformClock()),
                Factory, null!, null!, new TrackMatchDecisionEngine(), null!)
            { ControllerContext = new() { HttpContext = http } };
        }

        public ValueTask DisposeAsync() => database.DisposeAsync();
    }

    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static CanonicalRecordingRecord Recording(Guid owner, string title, DateTimeOffset at)
    {
        var id = Guid.CreateVersion7();
        return new()
        {
            Id = id,
            CreatedByUserId = owner,
            Title = title,
            PublicId = CanonicalRecordingRecord.DefaultPublicId(id),
            CreatedAt = at,
            UpdatedAt = at,
            Revision = 1
        };
    }
}
