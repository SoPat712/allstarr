using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class ManagedFilesControllerTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "allstarr-managed-controller", Guid.NewGuid().ToString("N"));
    private readonly Guid tenant = Guid.CreateVersion7();
    private readonly Guid owner = Guid.CreateVersion7();
    private readonly Guid otherUser = Guid.CreateVersion7();
    private DbFactory factory = null!;
    private AllstarrDbContext removalContext = null!;
    private ManagedFileOwnershipEntity owned = null!;
    private ManagedFileOwnershipEntity other = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        var options = new DbContextOptionsBuilder<AllstarrDbContext>()
            .UseSqlite($"Data Source={Path.Combine(root, "managed-controller.db")}").Options;
        factory = new(options);
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
        db.Tenants.Add(new TenantRecord { Id = tenant, Slug = "managed", Name = "Managed", CreatedAt = DateTimeOffset.UtcNow });
        db.Users.AddRange(User(owner), User(otherUser));
        owned = File(owner, "owned.flac", 'a');
        other = File(otherUser, "other.flac", 'b');
        db.ManagedFiles.AddRange(owned, other);
        await db.SaveChangesAsync();
        removalContext = new AllstarrDbContext(options);
    }

    [Fact]
    public async Task OwnerList_IsScopedAndNeverLeaksCanonicalPathRootScopeOrHash()
    {
        var result = Assert.IsType<OkObjectResult>(await Controller(Session(owner)).List());
        var serialized = JsonSerializer.Serialize(result.Value);
        using var json = JsonDocument.Parse(serialized);
        var files = json.RootElement.GetProperty("files");

        Assert.Single(files.EnumerateArray());
        Assert.Equal(owned.Id, files[0].GetProperty("Id").GetGuid());
        Assert.DoesNotContain(owned.CanonicalPath, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(owned.TargetRootPath, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(owned.ContentSha256, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(owned.ScopeKey, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(other.Id.ToString(), serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OwnerCannotDiscoverOrRemoveAnotherUsersFile()
    {
        var controller = Controller(Session(owner));

        Assert.IsType<NotFoundResult>(await controller.Remove(other.Id, new() { ExplicitlyConfirmed = true }, default));
        Assert.True(System.IO.File.Exists(other.CanonicalPath));
    }

    [Fact]
    public async Task RemovalRequiresConfirmationAndDoesNotLeakPathOrHash()
    {
        var result = Assert.IsType<BadRequestObjectResult>(await Controller(Session(owner)).Remove(
            owned.Id, new() { ExplicitlyConfirmed = false }, default));
        var serialized = JsonSerializer.Serialize(result.Value);

        Assert.True(System.IO.File.Exists(owned.CanonicalPath));
        Assert.DoesNotContain(owned.CanonicalPath, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(owned.ContentSha256, serialized, StringComparison.Ordinal);
        Assert.Contains("managed_file_confirmation_required", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OwnerCanExplicitlyRemoveOwnSingleReferenceFile()
    {
        Assert.IsType<NoContentResult>(await Controller(Session(owner)).Remove(
            owned.Id, new() { ExplicitlyConfirmed = true }, default));

        Assert.False(System.IO.File.Exists(owned.CanonicalPath));
        await using var db = await factory.CreateDbContextAsync();
        var stored = await db.ManagedFiles.SingleAsync(item => item.Id == owned.Id);
        Assert.NotNull(stored.RemovedAt);
        Assert.Equal(0, stored.ReferenceCount);
    }

    [Fact]
    public async Task AdministratorCanListAndRemoveAcrossOwners()
    {
        var controller = Controller(Session(owner, administrator: true));
        var list = Assert.IsType<OkObjectResult>(await controller.List());
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(list.Value));
        Assert.Equal(2, json.RootElement.GetProperty("files").GetArrayLength());

        Assert.IsType<NoContentResult>(await controller.Remove(other.Id, new() { ExplicitlyConfirmed = true }, default));
        Assert.False(System.IO.File.Exists(other.CanonicalPath));
    }

    [Fact]
    public async Task MissingSessionIsUnauthorizedWithoutFileDetails()
    {
        var controller = Controller(null);
        var result = Assert.IsType<UnauthorizedObjectResult>(await controller.Remove(
            owned.Id, new() { ExplicitlyConfirmed = true }, default));
        var serialized = JsonSerializer.Serialize(result.Value);
        Assert.DoesNotContain(owned.CanonicalPath, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(owned.ContentSha256, serialized, StringComparison.Ordinal);
    }

    private ManagedFilesController Controller(AdminAuthSession? session)
    {
        var context = new DefaultHttpContext();
        if (session is not null) context.Items[AdminAuthSessionService.HttpContextSessionItemKey] = session;
        return new(factory, new ManagedFileRemovalService(new EfManagedFileOwnershipStore(removalContext)))
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private AdminAuthSession Session(Guid userId, bool administrator = false) => new()
    {
        SessionId = Guid.NewGuid().ToString("N"),
        UserId = userId.ToString(),
        UserName = "fixture",
        IsAdministrator = administrator,
        TenantId = tenant,
        AllstarrUserId = userId,
        JellyfinAccessToken = "secret",
        ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
        LastSeenUtc = DateTime.UtcNow
    };

    private PlatformUserRecord User(Guid id) => new()
    {
        Id = id,
        TenantId = tenant,
        DisplayName = id.ToString("N"),
        Status = PlatformUserStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private ManagedFileOwnershipEntity File(Guid user, string name, char hashCharacter)
    {
        var targetRoot = Path.Combine(root, "library", user.ToString("N"));
        Directory.CreateDirectory(targetRoot);
        var path = Path.Combine(targetRoot, name);
        System.IO.File.WriteAllText(path, name);
        return new()
        {
            Id = Guid.CreateVersion7(),
            RootId = Guid.CreateVersion7(),
            TargetRootPath = targetRoot,
            CanonicalPath = path,
            ContentSha256 = new string(hashCharacter, 64),
            Length = new FileInfo(path).Length,
            PlacementMethod = ManagedFilePlacementMethod.Copy,
            TenantId = tenant,
            OwnerUserId = user,
            LibraryScopeId = "library",
            ScopeKey = $"scope-{user:N}",
            ReferenceCount = 1,
            IsManaged = true,
            CreatedAt = DateTimeOffset.UtcNow,
            Revision = 1
        };
    }

    public async Task DisposeAsync()
    {
        await removalContext.DisposeAsync();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed class DbFactory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AllstarrDbContext(options));
    }
}
