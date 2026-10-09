using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class LibraryIndexControllerTests : IAsyncLifetime
{
    private readonly Guid _viewerId = Guid.CreateVersion7();
    private readonly Guid _indexOwnerId = Guid.CreateVersion7();
    private readonly DateTimeOffset _now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private SqliteTestDatabase _database = null!;
    private TestDbContextFactory _factory = null!;
    private DurableJobQueue _queue = null!;
    private TestBackendLibraryAccess _access = null!;

    public async Task InitializeAsync()
    {
        _database = await SqliteTestDatabase.CreateAsync();
        _factory = new TestDbContextFactory(_database.Options);
        await using var db = await _factory.CreateDbContextAsync();
        db.Users.AddRange(User(_viewerId, "viewer", true), User(_indexOwnerId, "index-owner"));
        await db.SaveChangesAsync();
        var options = new DurableJobOptions();
        _queue = new DurableJobQueue(_factory, options, new JobPayloadPolicy(options), new SystemPlatformClock());
        _access = new TestBackendLibraryAccess(_factory, "music", "secondary");
    }

    [Fact]
    public async Task NonAdministratorCannotEnqueueOrReadCounts()
    {
        var controller = Controller(admin: false);

        Assert.Equal(StatusCodes.Status403Forbidden,
            Assert.IsType<ObjectResult>(await controller.Enqueue(new(), CancellationToken.None)).StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden,
            Assert.IsType<ObjectResult>(await controller.Counts(null, CancellationToken.None)).StatusCode);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.Jobs.ToListAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task EnqueueNormalizesAllLibrariesAndReusesTheSameGeneration(string? backendLibraryId)
    {
        var controller = Controller();
        var first = Assert.IsType<AcceptedResult>(await controller.Enqueue(
            new(Generation: 42), CancellationToken.None));
        var repeat = Assert.IsType<AcceptedResult>(await controller.Enqueue(
            new(backendLibraryId, Generation: 42), CancellationToken.None));

        using var firstJson = JsonDocument.Parse(JsonSerializer.Serialize(first.Value));
        using var repeatJson = JsonDocument.Parse(JsonSerializer.Serialize(repeat.Value));
        Assert.Equal(firstJson.RootElement.GetProperty("jobId").GetGuid(),
            repeatJson.RootElement.GetProperty("jobId").GetGuid());
        Assert.False(repeatJson.RootElement.GetProperty("created").GetBoolean());
        await using var db = await _factory.CreateDbContextAsync();
        var job = Assert.Single(await db.Jobs.ToListAsync());
        Assert.Equal(_viewerId, job.OwnerUserId);
        Assert.EndsWith(":backend:all:42", job.IdempotencyKey);
        var payload = JsonSerializer.Deserialize<LibraryIndexJobPayload>(job.PayloadJson)!;
        Assert.Null(payload.BackendLibraryId);
        Assert.Equal("backend", payload.BackendInstanceId);
        Assert.Equal("viewer", payload.BackendPrincipalId);
    }

    [Fact]
    public async Task EnqueueTrimsExplicitLibraryInPayloadScopeAndKey()
    {
        Assert.IsType<AcceptedResult>(await Controller().Enqueue(new(" music ", Generation: 42), CancellationToken.None));

        await using var db = await _factory.CreateDbContextAsync();
        var job = Assert.Single(await db.Jobs.ToListAsync());
        Assert.Equal(_viewerId, job.OwnerUserId);
        Assert.Equal("music", JsonSerializer.Deserialize<LibraryIndexJobPayload>(job.PayloadJson)!.BackendLibraryId);
        Assert.EndsWith(":backend:library:music:42", job.IdempotencyKey);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public async Task EnqueueRejectsInvalidPageSize(int pageSize)
    {
        Assert.IsType<BadRequestObjectResult>(await Controller().Enqueue(new(PageSize: pageSize), CancellationToken.None));
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.Jobs.ToListAsync());
    }

    [Fact]
    public async Task EnqueueAndCountsRequireTheExactLinkedBackendPrincipal()
    {
        var controller = Controller(principal: "unlinked");

        Assert.Equal(StatusCodes.Status403Forbidden,
            Assert.IsType<ObjectResult>(await controller.Enqueue(new(), CancellationToken.None)).StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden,
            Assert.IsType<ObjectResult>(await controller.Counts(null, CancellationToken.None)).StatusCode);
    }

    [Theory]
    [InlineData(null, 2)]
    [InlineData("  ", 2)]
    [InlineData(" music ", 1)]
    [InlineData("secondary", 1)]
    [InlineData("denied", 0)]
    public async Task CountsUsesViewerLibrariesAcrossIndexOwnersAndDeduplicatesBackendItems(string? scope, int expected)
    {
        await SeedTracks();

        var result = Assert.IsType<OkObjectResult>(await Controller().Counts(scope, CancellationToken.None));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        Assert.Equal(expected, json.RootElement.GetProperty("trackCount").GetInt32());
        Assert.Equal("backend", json.RootElement.GetProperty("backendInstanceId").GetString());
        Assert.Equal(string.IsNullOrWhiteSpace(scope) ? null : scope.Trim(),
            json.RootElement.GetProperty("backendLibraryId").GetString());
        if (expected == 0) Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("lastIndexedAt").ValueKind);
        else Assert.Equal(_now, json.RootElement.GetProperty("lastIndexedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task CountsHidesTracksWhenBackendLibraryAccessIsUnavailable()
    {
        await SeedTracks();
        _access.Permissions[_viewerId] = BackendLibraryAccess.Unavailable;

        var result = Assert.IsType<OkObjectResult>(await Controller().Counts(null, CancellationToken.None));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        Assert.Equal(0, json.RootElement.GetProperty("trackCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("lastIndexedAt").ValueKind);
    }

    [Theory]
    [InlineData(null, 7)]
    [InlineData("music", 3)]
    public async Task CountsSelectsTheLatestAuditForAllOrExplicitBackendLibrary(string? scope, int seen)
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.AuditEvents.AddRange(Scan(null, 7, _now), Scan("music", 3, _now.AddMinutes(1)));
            await db.SaveChangesAsync();
        }

        var result = Assert.IsType<OkObjectResult>(await Controller().Counts(scope, CancellationToken.None));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        Assert.Equal(seen, json.RootElement.GetProperty("latestScan").GetProperty("seen").GetInt32());
    }

    private async Task SeedTracks()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var olderCopy = Track(_viewerId, "music", "shared-item");
        olderCopy.IndexedAt = _now.AddMinutes(-1);
        db.LibraryTracks.AddRange(
            olderCopy,
            Track(_indexOwnerId, "music", "shared-item"),
            Track(_indexOwnerId, "secondary", "second-item"),
            Track(_indexOwnerId, "denied", "hidden-item"),
            Track(_indexOwnerId, "music", "wrong-protocol", protocol: "subsonic"),
            Track(_indexOwnerId, "music", "wrong-backend", backend: "other"));
        await db.SaveChangesAsync();
    }

    private LibraryTrackRecord Track(Guid owner, string library, string item,
        string protocol = "jellyfin", string backend = "backend") => new()
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = owner,
            BackendLibraryId = library,
            Protocol = protocol,
            BackendInstanceId = backend,
            BackendItemId = item,
            FilePath = $"/fixture/{item}.flac",
            Title = "Fixture",
            Artist = "Artist",
            IndexedAt = _now,
            SourceModifiedAt = _now,
            UpdatedAt = _now
        };

    private AuditEventRecord Scan(string? scope, int seen, DateTimeOffset created) => new()
    {
        Id = Guid.CreateVersion7(),
        ActorUserId = _viewerId,
        Category = "library-index",
        Action = "scan.completed",
        Outcome = "success",
        CreatedAt = created,
        DetailsJson = JsonSerializer.Serialize(new
        {
            BackendLibraryId = scope,
            BackendInstanceId = "backend",
            Seen = seen,
            Indexed = seen,
            SkippedPathless = 0,
            SkippedMalformed = 0,
            Pages = 1
        })
    };

    private LibraryIndexController Controller(bool admin = true, string principal = "viewer")
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = Guid.NewGuid().ToString("N"),
            UserId = principal,
            BackendType = "jellyfin",
            BackendInstanceId = "backend",
            UserName = "Fixture",
            IsAdministrator = admin,
            AllstarrUserId = _viewerId,
            JellyfinAccessToken = "protected",
            ExpiresAtUtc = _now.UtcDateTime.AddHours(1),
            LastSeenUtc = _now.UtcDateTime
        };
        return new LibraryIndexController(_factory, _queue, _access)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
    }

    private UserRecord User(Guid id, string principal, bool admin = false) => new()
    {
        Id = id,
        DisplayName = id.ToString("N"),
        BackendType = "jellyfin",
        BackendInstanceId = "backend",
        BackendPrincipalId = principal,
        IsAdmin = admin,
        Enabled = true,
        CreatedAt = _now,
        UpdatedAt = _now,
        LastSeenAt = _now
    };

    public async Task DisposeAsync() => await _database.DisposeAsync();

    private sealed class TestDbContextFactory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AllstarrDbContext(options));
    }
}
