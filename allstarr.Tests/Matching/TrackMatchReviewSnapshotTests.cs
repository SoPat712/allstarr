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

public sealed class TrackMatchReviewSnapshotTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("Retained")]
    [Trait("Category", "Sqlite")]
    public async Task ReviewFiltersKindsAndInvalidLabelsBeforeTheScanLimit(string? search)
    {
        await using var fixture = await Fixture.CreateAsync();
        var review = await fixture.Service.GetReviewDataAsync(new(fixture.Owner, false), search: search, scanLimit: 1);
        Assert.Equal(fixture.TrackId, Assert.Single(review.Snapshots).Id);
        await using var db = fixture.Factory.CreateDbContext();
        Assert.Equal(11, await db.ExternalMetadataSnapshots.CountAsync());
        Assert.Equal("{\"members\":[]}", (await db.ExternalMetadataSnapshots.SingleAsync(item => item.ResourceKind == "playlist-membership")).PayloadJson);
        Assert.Contains("DurationSeconds", (await db.ExternalMetadataSnapshots.FirstAsync(item => item.ResourceKind == "track-media")).PayloadJson);
    }

    [Fact]
    [Trait("Category", "Sqlite")]
    public async Task ReviewCountsSearchAndPagesContainOnlyTracksAndKeepManualChoices()
    {
        await using var fixture = await Fixture.CreateAsync();
        var controller = fixture.Controller();
        var first = Json(await controller.List(pageSize: 1));
        Assert.Equal(2, first.GetProperty("stats").GetProperty("total").GetInt32());
        Assert.Equal(1, first.GetProperty("stats").GetProperty("manualMatches").GetInt32());
        Assert.Equal(2, first.GetProperty("pagination").GetProperty("totalPages").GetInt32());
        var track = Assert.Single(first.GetProperty("matches").EnumerateArray());
        Assert.Equal(fixture.TrackId, track.GetProperty("externalSnapshotId").GetGuid());
        Assert.Equal("Retained title", track.GetProperty("title").GetString());
        Assert.Equal("Retained artist", track.GetProperty("artist").GetString());
        Assert.Equal(244220, track.GetProperty("durationMilliseconds").GetInt64());
        Assert.Equal("flac", track.GetProperty("media").GetProperty("codec").GetString());
        Assert.Equal("qobuz", Assert.Single(track.GetProperty("manualAuthorities").EnumerateArray()).GetProperty("targetProviderId").GetString());
        var second = Json(await controller.List(page: 2, pageSize: 1));
        Assert.Equal("Older title", Assert.Single(second.GetProperty("matches").EnumerateArray()).GetProperty("title").GetString());
        var found = Json(await controller.List(search: "Retained artist", pageSize: 1));
        Assert.Equal(1, found.GetProperty("pagination").GetProperty("total").GetInt32());
        var missing = Json(await controller.List(search: "diagnostic"));
        Assert.Empty(missing.GetProperty("matches").EnumerateArray());
        Assert.Empty(Json(await controller.List(externalSnapshotId: fixture.MediaId)).GetProperty("matches").EnumerateArray());
        await using var db = fixture.Factory.CreateDbContext();
        Assert.Single(await db.ManualTrackOverrides.ToArrayAsync());
        Assert.Empty(await db.TrackMatches.ToArrayAsync());
    }

    [Theory]
    [InlineData("track-media")]
    [InlineData("playlist-membership")]
    [InlineData("playback-metadata")]
    [Trait("Category", "Sqlite")]
    public async Task NonTrackSnapshotsCannotBeRematchedResolvedOrOverridden(string kind)
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var db = fixture.Factory.CreateDbContext();
        var snapshot = await db.ExternalMetadataSnapshots.FirstAsync(item => item.ResourceKind == kind);
        Assert.Equal(snapshot.Id, (await fixture.Service.FindSnapshotAsync(snapshot.Id))!.Id);
        var actor = new TrackMatchActor(fixture.Owner, false);
        Assert.Equal(TrackMatchCommandFailure.Invalid, (await fixture.Service.RematchSnapshotAsync(actor, snapshot.Id, "review")).Failure);
        Assert.Equal(TrackMatchCommandFailure.Invalid, (await fixture.Service.ResolveSnapshotAsync(actor, snapshot.Id, new("reject"), "review")).Failure);
        var controller = fixture.Controller();
        Assert.IsType<BadRequestObjectResult>(await controller.Rematch(snapshot.Id));
        Assert.IsType<BadRequestObjectResult>(await controller.Resolve(snapshot.Id, new("reject")));
        Assert.Equal(TrackMatchCommandFailure.Invalid, (await fixture.Service.ClearManualAuthorityAsync(actor, snapshot.Id,
            ManualTrackAuthorityKind.ProviderMatch, Guid.CreateVersion7(), 1, "review")).Failure);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.SetOverrideAsync(fixture.Protocol,
            new(snapshot.Id, ManualOverrideDecision.Reject, null, "review")));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.RecordDecisionAsync(fixture.Protocol,
            new(snapshot.Id, null, null, TrackMatchState.Unresolved, 0, .88, 1, 1, 0, "fixture", "fixture", "[]", "[]", "[]")));
        Assert.Empty(await db.TrackMatches.ToArrayAsync());
        Assert.Single(await db.ManualTrackOverrides.ToArrayAsync());
        Assert.Equal(11, await db.ExternalMetadataSnapshots.CountAsync());
    }

    [Fact]
    [Trait("Category", "Sqlite")]
    public async Task ReviewDoesNotUseForeignOwnerOrAccountMetadata()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var db = fixture.Factory.CreateDbContext();
        var foreign = Guid.CreateVersion7();
        var account = Guid.CreateVersion7();
        db.Users.Add(new() { Id = foreign, DisplayName = "Other listener", Enabled = true });
        db.ProviderAccounts.Add(new() { Id = account, OwnerUserId = foreign, ProviderId = "spotify", Enabled = true });
        var foreignTrack = fixture.Snapshot("track", "private", "{\"Title\":\"Private title\",\"Artist\":\"Private artist\"}");
        foreignTrack.OwnerUserId = foreign;
        db.ExternalMetadataSnapshots.Add(foreignTrack);
        var foreignAccount = fixture.Snapshot("track", "private-account", foreignTrack.PayloadJson);
        foreignAccount.ProviderAccountId = account;
        db.ExternalMetadataSnapshots.Add(foreignAccount);
        var media = fixture.Snapshot("track-media", "source", "{\"DurationSeconds\":999,\"Media\":{\"Codec\":\"mp3\"}}", version: 2);
        media.ProviderAccountId = account;
        db.ExternalMetadataSnapshots.Add(media);
        await db.SaveChangesAsync();
        var review = await fixture.Service.GetReviewDataAsync(new(fixture.Owner, false));
        Assert.Equal(2, review.Snapshots.Count);
        var value = Json(await fixture.Controller().List());
        var row = Assert.Single(value.GetProperty("matches").EnumerateArray(), item => item.GetProperty("externalSnapshotId").GetGuid() == fixture.TrackId);
        Assert.Equal(244220, row.GetProperty("durationMilliseconds").GetInt64());
        Assert.Equal("flac", row.GetProperty("media").GetProperty("codec").GetString());
    }

    [Theory]
    [InlineData("missing-media")]
    [InlineData("invalid-json")]
    [Trait("Category", "Sqlite")]
    public async Task MalformedMediaFactsCannotSupplyLabelsOrBreakReview(string shape)
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var db = fixture.Factory.CreateDbContext();
        var media = await db.ExternalMetadataSnapshots.SingleAsync(item => item.Id == fixture.MediaId);
        var malformed = fixture.Snapshot("track-media", "source",
            shape == "missing-media" ? """{"DurationSeconds":900,"Title":"Wrong"}""" : "[]", version: 2,
            at: media.RetrievedAt.AddMinutes(1));
        db.ExternalMetadataSnapshots.Add(malformed);
        await db.SaveChangesAsync();
        var row = Assert.Single(Json(await fixture.Controller().List(externalSnapshotId: fixture.TrackId)).GetProperty("matches").EnumerateArray());
        Assert.Equal("Retained title", row.GetProperty("title").GetString());
        Assert.Equal("Retained artist", row.GetProperty("artist").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("durationMilliseconds").ValueKind);
    }

    private static JsonElement Json(IActionResult result) => JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private sealed class Fixture(SqliteTestDatabase database) : IAsyncDisposable
    {
        private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        public Factory Factory { get; } = new(database.Options);
        public Guid Owner { get; } = Guid.CreateVersion7();
        public Guid TrackId { get; private set; }
        public Guid MediaId { get; private set; }
        public TrackMatchCommandService Service { get; private set; } = null!;
        public ProtocolExecutionContext Protocol => new(ProtocolKind.Jellyfin, "fixture", "listener",
            new AllstarrPrincipal(Owner, "jellyfin", "fixture", "listener", "Listener", false), "review",
            DateTimeOffset.UtcNow.AddMinutes(1), default);

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture(await SqliteTestDatabase.CreateAsync());
            await using var db = fixture.Factory.CreateDbContext();
            db.Users.Add(new() { Id = fixture.Owner, BackendType = "jellyfin", BackendInstanceId = "fixture", BackendPrincipalId = "listener", DisplayName = "Listener", Enabled = true });
            var track = fixture.Snapshot("track", "source", "{\"Title\":\"Retained title\",\"Artist\":\"Retained artist\"}", at: Now);
            fixture.TrackId = track.Id;
            var media = fixture.Snapshot("track-media", "source", "{\"Title\":\"diagnostic\",\"Artist\":\"diagnostic\",\"DurationSeconds\":244.22,\"Media\":{\"MimeType\":\"audio/flac\",\"Container\":\"flac\",\"Codec\":\"flac\"}}");
            fixture.MediaId = media.Id;
            db.ExternalMetadataSnapshots.AddRange(track, media,
                fixture.Snapshot("track", "older", "{\"Title\":\"Older title\",\"Artists\":[{\"Name\":\"Older artist\"}]}", at: Now.AddMinutes(-1)),
                fixture.Snapshot("track-media", "only-media", media.PayloadJson),
                fixture.Snapshot("playlist-membership", "playlist", "{\"members\":[]}", "allstarr"),
                fixture.Snapshot("playback-metadata", "source", "{\"Title\":\"diagnostic\",\"Artist\":\"diagnostic\"}"),
                fixture.Snapshot("track", "internal", """{"Title":"Internal title","Artist":"Internal artist"}""", "allstarr"),
                fixture.Snapshot("track", "missing-title", "{\"Artist\":\"Retained artist\"}"),
                fixture.Snapshot("track", "missing-artist", "{\"Title\":\"Retained title\"}"),
                fixture.Snapshot("track", "malformed", "{\"Title\":{},\"Artists\":[null,7]}"),
                fixture.Snapshot("track", "blank", """{"Title":"  ","Artist":"  "}"""));
            db.ManualTrackOverrides.Add(new()
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = fixture.Owner,
                ExternalSnapshotId = track.Id,
                SourceProviderId = "spotify",
                SourceExternalIdHash = track.ExternalIdHash,
                Decision = ManualOverrideDecision.Pin,
                TargetProviderId = "qobuz",
                TargetExternalId = "kept-target",
                Reason = "Preferred source",
                DecisionVersion = 1,
                Revision = 1,
                CreatedAt = Now
            });
            await db.SaveChangesAsync();
            fixture.Service = new(fixture.Factory, new TrackMatchDecisionEngine(), new ProviderAccountResolver(fixture.Factory),
                new SystemPlatformClock(), new TestBackendLibraryAccess(fixture.Factory, "music"));
            return fixture;
        }

        public ExternalMetadataSnapshotRecord Snapshot(string kind, string id, string payload, string provider = "spotify", int version = 1, DateTimeOffset? at = null) => new()
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = Owner,
            ProviderId = provider,
            ResourceKind = kind,
            ExternalIdHash = Hash(id),
            PayloadJson = payload,
            PayloadSha256 = Hash(payload),
            SnapshotVersion = version,
            ProviderRevision = kind == "track-media" ? "media:0" : "fixture",
            BackendInstanceId = "fixture",
            BackendPrincipalId = "listener",
            Protocol = "jellyfin",
            RetrievedAt = at ?? Now.AddMinutes(1)
        };

        public TrackMatchesController Controller()
        {
            var gateway = new Mock<IProtocolProviderGateway>();
            gateway.Setup(item => item.GetPlayableProviderOrderAsync(It.IsAny<ProviderActorContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(new[] { "qobuz" });
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
            return new(Service, gateway.Object, new AdminProtocolExecutionContextFactory(Factory, new SystemPlatformClock()),
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
}
