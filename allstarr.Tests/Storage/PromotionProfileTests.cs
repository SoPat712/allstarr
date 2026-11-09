using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Enrichment;
using allstarr.Core.Identity;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Middleware;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class PromotionProfileTests : IAsyncLifetime
{
    private readonly Guid listener = Guid.CreateVersion7();
    private readonly Guid administrator = Guid.CreateVersion7();
    private SqliteTestDatabase database = null!;
    private Factory factory = null!;
    private PromotionProfiles profiles = null!;
    private TestBackendLibraryAccess access = null!;

    public async Task InitializeAsync()
    {
        database = await SqliteTestDatabase.CreateAsync();
        factory = new(database.Options);
        access = new(factory, "music");
        profiles = new(factory);
        await using var db = factory.CreateDbContext();
        db.Users.AddRange(User(listener, false), User(administrator, true));
        await db.SaveChangesAsync();
    }

    [Fact]
    public void SongFieldsExposeDatesDiscCountsQualityAndSupportedTags()
    {
        var fields = PromotionSongFields.FromSong(new()
        {
            Title = "Title",
            Artist = "Artist",
            AlbumArtist = "Various Artists",
            Album = "Album",
            ReleaseDate = "2024-05-02",
            Track = 3,
            TotalTracks = 20,
            DiscNumber = 2,
            TotalDiscs = 3,
            Codec = "flac",
            BitDepth = 24,
            SampleRate = 96000,
            Genre = "Jazz",
            Label = "Label",
            ProviderIds = new() { ["originaldate"] = "1998-01-01", ["musicbrainz_albumid"] = "release", ["catalognumber"] = "CAT-1" }
        }, ".FLAC");
        Assert.Equal("2024", fields["year"]);
        Assert.Equal("2024-05-02", fields["date"]);
        Assert.Equal("1998", fields["originalyear"]);
        Assert.Equal("FLAC 24-96", fields["_quality"]);
        Assert.Equal("flac", fields["_extension"]);
        Assert.Equal("2", fields["discnumber"]);
        Assert.Equal("3", fields["totaldiscs"]);
        Assert.Equal("Various Artists", fields["albumartist"]);
        Assert.Equal("release", fields["musicbrainz_albumid"]);
        Assert.Equal("CAT-1", fields["catalognumber"]);
    }

    [Fact]
    public async Task DefaultsAndHouseholdFallbackPersistAcrossStoreInstances()
    {
        var initial = await profiles.GetMineAsync(Context(listener));
        Assert.True(initial.UsesHouseholdDefault);
        Assert.Equal("lossless", initial.Values.QualityFloor.CodecFamily);
        Assert.Equal(16, initial.Values.QualityFloor.MinBitDepth);
        Assert.Equal(44100, initial.Values.QualityFloor.MinSampleRate);
        Assert.Equal(new PromotionQualityCeiling(24, 96000), initial.Values.HiResCeiling);
        Assert.True(initial.Values.FakeLosslessCheck);
        Assert.False(initial.Values.AllowLossy);
        Assert.True(initial.Values.MusicBrainzTags);
        Assert.True(initial.Values.ReplayGain);
        Assert.Equal("album", initial.Values.DefaultUnit);
        Assert.Equal("admins-and-trusted-listeners", initial.HouseholdDefault.AddWithoutApproval);

        var household = initial.HouseholdDefault with { Values = initial.Values with { NamingScript = "%artist%/%title%.%_extension%" } };
        await profiles.SaveHouseholdAsync(Context(administrator), household);
        var read = await new PromotionProfiles(factory).GetMineAsync(Context(listener));
        Assert.Equal(household, read.HouseholdDefault);
        Assert.Equal(household.Values, read.Values);
        Assert.True(read.UsesHouseholdDefault);
    }

    [Fact]
    public async Task PersonalProfileRoundTripsEveryFieldAndIsIsolatedFromHouseholdChanges()
    {
        var personal = new PromotionProfile
        {
            NamingScript = "%title%.%_extension%",
            LibraryRootId = Guid.CreateVersion7(),
            QualityFloor = new("lossy", 0, 22050),
            HiResCeiling = new(32, 192000),
            FakeLosslessCheck = false,
            AllowLossy = true,
            MusicBrainzTags = false,
            CoverMode = "cover.jpg",
            LyricsMode = ".lrc",
            ReplayGain = false,
            DefaultUnit = "song",
            RemoveKeptCopy = true
        };
        var previous = System.Text.Json.JsonSerializer.Deserialize<PromotionProfile>("{\"AfterAdding\":false,\"RemoveKeptCopy\":true}");
        Assert.True(previous!.RemoveKeptCopy);
        await profiles.SaveMineAsync(Context(listener), personal);
        await profiles.SaveHouseholdAsync(Context(administrator), new(new() { NamingScript = "%album%/%title%.%_extension%" }, "admins"));
        var read = await new PromotionProfiles(factory).GetMineAsync(Context(listener));
        Assert.Equal(personal, read.Values);
        Assert.False(read.UsesHouseholdDefault);
        Assert.True((await profiles.GetMineAsync(Context(administrator))).UsesHouseholdDefault);
        await profiles.SaveMineAsync(Context(listener), null);
        Assert.True((await profiles.GetMineAsync(Context(listener))).UsesHouseholdDefault);
    }

    [Fact]
    public async Task HouseholdRequiresAdministratorAndEveryOperationRequiresExactEnabledIdentity()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => profiles.GetHouseholdAsync(Context(listener)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => profiles.SaveHouseholdAsync(Context(listener), new(new())));
        var context = Context(listener, "other");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => profiles.GetMineAsync(context));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => profiles.SaveMineAsync(context, new()));
        await using var db = factory.CreateDbContext();
        (await db.Users.SingleAsync(item => item.Id == listener)).Enabled = false;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => profiles.GetMineAsync(Context(listener)));
    }

    [Fact]
    public async Task ValidationRejectsBadScriptsQualityModesAndHouseholdPolicyWithoutSaving()
    {
        var error = await Assert.ThrowsAsync<PromotionProfileValidationException>(() =>
            profiles.SaveMineAsync(Context(listener), new() { NamingScript = "%title%/$unknown(x)" }));
        Assert.Contains(error.Errors, item => item.Position == 8);
        await Assert.ThrowsAsync<ArgumentException>(() => profiles.SaveMineAsync(Context(listener), new() { CoverMode = "invalid" }));
        await Assert.ThrowsAsync<ArgumentException>(() => profiles.SaveMineAsync(Context(listener), new() { LibraryRootId = Guid.Empty }));
        await Assert.ThrowsAsync<ArgumentException>(() => profiles.SaveMineAsync(Context(listener), new() { QualityFloor = new("lossless", 24, 192000) }));
        await Assert.ThrowsAsync<ArgumentException>(() => profiles.SaveMineAsync(Context(listener), new() { QualityFloor = new("lossy", 0, 44100) }));
        await Assert.ThrowsAsync<ArgumentException>(() => profiles.SaveHouseholdAsync(Context(administrator), new(new(), "everyone")));
        Assert.True((await profiles.GetMineAsync(Context(listener))).UsesHouseholdDefault);
    }

    [Fact]
    public async Task PreviewSelectsRealExamplesAndExcludesDeniedLibrariesAndForeignKeptSongs()
    {
        var album = await SeedSong("Album song", "Album", 1, 1, "Artist");
        var multi = await SeedSong("Disc song", "Double", 2, 2, "Artist");
        var compilation = await SeedSong("Guest song", "Collection", 1, 1, "Various Artists");
        var single = await SeedSong("Single song", "", 1, 1, "Artist");
        var hidden = await SeedSong("Private song", "Private", 1, 1, "Artist", "denied");
        var preview = new PromotionNamingPreviewService(factory, profiles, access);
        var result = await preview.PreviewAsync(Context(listener), new());
        Assert.Equal(new[] { album, multi, compilation, single }.Order(), result.Examples.Select(item => item.LibraryTrackId!.Value).Order());
        Assert.Contains(result.Examples, item => item.Kind == "multi-disc" && item.RelativePath!.Contains("2-01"));
        Assert.Contains(result.Examples, item => item.Kind == "compilation" && item.RelativePath!.StartsWith("Various Artists/"));
        Assert.All(result.Examples, item => Assert.Empty(item.Errors));
        Assert.DoesNotContain(result.Examples, item => item.Title == "Private song");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => preview.PreviewAsync(Context(listener), new(LibraryTrackIds: [hidden])));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => preview.PreviewAsync(Context(listener), new(RecordingIds: [Guid.NewGuid()])));
        access.Permissions[listener] = BackendLibraryAccess.Unavailable;
        Assert.Empty((await preview.PreviewAsync(Context(listener), new())).Examples);
    }

    [Fact]
    public async Task PreviewFindsDistinctExamplesBeyondTheFirstLibraryPage()
    {
        var multi = await SeedSong("Late disc", "Double", 2, 2, "Artist");
        await using (var db = factory.CreateDbContext())
        {
            (await db.LibraryTracks.SingleAsync(item => item.Id == multi)).BackendItemId = "zzzz-disc";
            for (var index = 0; index < 520; index++) db.LibraryTracks.Add(new()
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = listener,
                BackendLibraryId = "music",
                Protocol = "jellyfin",
                BackendInstanceId = "backend",
                BackendItemId = $"album-{index:0000}",
                FilePath = "/fixture/song.flac",
                Title = "Album song",
                Artist = "Artist",
                Album = "Album"
            });
            await db.SaveChangesAsync();
        }
        var result = await new PromotionNamingPreviewService(factory, profiles, access).PreviewAsync(Context(listener), new());
        Assert.Equal(4, result.Examples.Count);
        Assert.Contains(result.Examples, item => item.LibraryTrackId == multi && item.Kind == "multi-disc");
    }

    [Fact]
    public async Task PreviewUsesPostedScriptReportsPositionsAndDoesNotPersistIt()
    {
        await SeedSong("Real/title: song", "Album", 1, 1, "Artist");
        var preview = new PromotionNamingPreviewService(factory, profiles, access);
        var result = await preview.PreviewAsync(Context(listener), new("%title%.%_extension%"));
        Assert.False(result.ScriptOnly);
        Assert.DoesNotContain('/', Assert.Single(result.Examples).RelativePath!);
        var invalid = await preview.PreviewAsync(Context(listener), new("%title%/$unknown(x)"));
        Assert.Contains(invalid.Errors, error => error.Position == 8 && error.Length > 0);
        Assert.NotEqual(result.Script, (await profiles.GetMineAsync(Context(listener))).Values.NamingScript);
        Assert.True((await preview.PreviewAsync(Context(listener), new("$upper(%title%).%_extension%"))).ScriptOnly);
    }

    [Fact]
    public async Task ControllerRequiresSessionAndAdministratorAndReturnsPositionedErrors()
    {
        var controller = Controller();
        Assert.IsType<UnauthorizedResult>(await controller.Mine(default));
        controller = Controller(listener);
        Assert.IsType<OkObjectResult>(await controller.Mine(default));
        Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.Household(default)).StatusCode);
        var invalid = Assert.IsType<BadRequestObjectResult>(await controller.SaveMine(new() { NamingScript = "$bad(x)" }, default));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(invalid.Value));
        Assert.Equal(0, json.RootElement.GetProperty("errors")[0].GetProperty("Position").GetInt32());
        Assert.IsType<OkObjectResult>(await Controller(administrator).Household(default));
    }

    [Theory]
    [InlineData("GET", "me", true)]
    [InlineData("PUT", "me", true)]
    [InlineData("DELETE", "me", true)]
    [InlineData("POST", "preview", true)]
    [InlineData("GET", "household", false)]
    [InlineData("PUT", "household", false)]
    [InlineData("PUT", "preview", false)]
    [InlineData("GET", "me/extra", false)]
    public void ListenerRoutePolicyAllowsOnlyPersonalProfileAndPreview(string method, string suffix, bool allowed)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        http.Request.Path = "/api/admin/library-promotions/profiles/" + suffix;
        var policy = typeof(AdminAuthenticationMiddleware).GetMethod("IsAllowedForNonAdministrator", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(allowed, policy.Invoke(null, [http.Request]));
    }

    private async Task<Guid> SeedSong(string title, string album, int disc, int totalDiscs, string albumArtist, string library = "music")
    {
        await using var db = factory.CreateDbContext();
        var recordingId = Guid.CreateVersion7();
        var identityId = Guid.CreateVersion7();
        var id = Guid.CreateVersion7();
        var externalHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id.ToString())));
        db.CanonicalRecordings.Add(new() { Id = recordingId, CreatedByUserId = listener, PublicId = id.ToString(), Title = title });
        db.LibraryTracks.Add(new()
        {
            Id = id,
            OwnerUserId = listener,
            CanonicalRecordingId = recordingId,
            BackendLibraryId = library,
            Protocol = "jellyfin",
            BackendInstanceId = "backend",
            BackendItemId = id.ToString(),
            FilePath = "/fixture/song.flac",
            Title = title,
            Artist = "Artist",
            Album = album,
            AlbumArtist = albumArtist
        });
        db.ProviderTrackIdentities.Add(new()
        {
            Id = identityId,
            CanonicalRecordingId = recordingId,
            ProviderId = "fixture",
            ExternalId = id.ToString(),
            ExternalIdHash = externalHash,
            ResourceKind = allstarr.Core.Capabilities.ProviderResourceKind.Track,
            Verification = ProviderIdentityVerification.Verified,
            VerificationMethod = "fixture",
            DecisionVersion = 1,
            Scope = ProviderIdentityScope.Catalog
        });
        var payload = JsonSerializer.Serialize(new allstarr.Models.Domain.Song
        {
            Title = title,
            Artist = "Artist",
            Album = album,
            AlbumArtist = albumArtist,
            ExternalProvider = "fixture",
            ExternalId = id.ToString(),
            Track = 1,
            DiscNumber = disc,
            TotalDiscs = totalDiscs,
            Container = "flac",
            Year = 2024
        });
        db.ExternalMetadataSnapshots.Add(new()
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = listener,
            ProviderTrackIdentityId = identityId,
            ProviderId = "fixture",
            ExternalIdHash = externalHash,
            ResourceKind = "track",
            BackendInstanceId = "backend",
            BackendPrincipalId = listener.ToString(),
            Protocol = "jellyfin",
            PayloadJson = payload,
            SnapshotVersion = 1,
            PayloadSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload))),
            ProviderRevision = "fixture",
            CorrelationId = "promotion-preview",
            RetrievedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return id;
    }

    private PromotionProfilesController Controller(Guid? user = null)
    {
        var http = new DefaultHttpContext();
        if (user.HasValue) http.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "profile-test",
            UserId = user.Value.ToString(),
            UserName = "Listener",
            BackendType = "jellyfin",
            BackendInstanceId = "backend",
            AllstarrUserId = user,
            IsAdministrator = user == administrator,
            JellyfinAccessToken = "protected",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            LastSeenUtc = DateTime.UtcNow
        };
        return new(profiles, new(factory, profiles, access), new(factory, new SystemPlatformClock()))
        { ControllerContext = new() { HttpContext = http } };
    }

    private ProtocolExecutionContext Context(Guid id, string? principal = null) => new(ProtocolKind.Jellyfin, "backend", principal ?? id.ToString(),
        new AllstarrPrincipal(id, "jellyfin", "backend", principal ?? id.ToString(), "Listener", id == administrator),
        "promotion-profile-test", DateTimeOffset.UtcNow.AddMinutes(5), default);

    private static UserRecord User(Guid id, bool admin) => new()
    {
        Id = id,
        Enabled = true,
        IsAdmin = admin,
        DisplayName = "Listener",
        BackendType = "jellyfin",
        BackendInstanceId = "backend",
        BackendPrincipalId = id.ToString()
    };

    public async Task DisposeAsync() => await database.DisposeAsync();

    private sealed class Factory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
