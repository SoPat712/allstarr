using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Identity;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Protocols.Jellyfin;
using allstarr.Core.Protocols.Subsonic;
using allstarr.Core.Storage;
using allstarr.Services.Subsonic;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class SubsonicPlaylistMutationTests : IAsyncLifetime
{
    private PostgresTestDatabase _database = null!;
    private Factory _factory = null!;
    private Guid _tenantId;
    private Guid _ownerId;
    private Guid _otherUserId;
    private Guid _accountId;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        _factory = new Factory(_database.Options);
        _tenantId = Guid.CreateVersion7();
        _ownerId = Guid.CreateVersion7();
        _otherUserId = Guid.CreateVersion7();
        _accountId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;

        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
        db.Tenants.Add(new TenantRecord
        {
            Id = _tenantId,
            Slug = "subsonic-playlist-mutation",
            Name = "Subsonic playlist mutation",
            CreatedAt = now
        });
        db.Users.AddRange(
            User(_ownerId, "Owner", now),
            User(_otherUserId, "Other", now));
        db.ProviderAccounts.Add(new ProviderAccountRecord
        {
            Id = _accountId,
            TenantId = _tenantId,
            OwnerUserId = _ownerId,
            ProviderId = "spotify",
            DisplayName = "Source",
            Scope = ProviderAccountScope.User,
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public void ReplaceValue_PreservesSourceOrderAndRepeatedMutationValues()
    {
        var request = new SubsonicRequestParameters(
            "POST",
            "application/x-www-form-urlencoded",
            null,
            [
                new("v", "1.16.1", SubsonicParameterSource.Query),
                new("playlistId", "allstarr-vpl-source", SubsonicParameterSource.Form),
                new("songIdToAdd", "song-a", SubsonicParameterSource.Form),
                new("songIdToAdd", "song-b", SubsonicParameterSource.Form),
                new("songIndexToRemove", "2", SubsonicParameterSource.Form),
                new("songIndexToRemove", "0", SubsonicParameterSource.Form)
            ]);

        var rewritten = request.ReplaceValue("playlistId", "backend-target");

        Assert.Equal(request.Method, rewritten.Method);
        Assert.Equal(request.ContentType, rewritten.ContentType);
        Assert.Equal(
            ["v=1.16.1", "playlistId=backend-target", "songIdToAdd=song-a",
                "songIdToAdd=song-b", "songIndexToRemove=2", "songIndexToRemove=0"],
            rewritten.Ordered.Select(item => $"{item.Name}={item.Value}"));
        Assert.Equal(SubsonicParameterSource.Query, rewritten.Ordered[0].Source);
        Assert.All(rewritten.Ordered.Skip(1), item =>
            Assert.Equal(SubsonicParameterSource.Form, item.Source));
        Assert.Equal(
            "playlistId=backend-target&songIdToAdd=song-a&songIdToAdd=song-b&songIndexToRemove=2&songIndexToRemove=0",
            rewritten.RawBody);
    }

    [Fact]
    public async Task Resolver_ReturnsOnlyExactScopedMaterializedOrHybridTarget()
    {
        var resolver = new SubsonicPlaylistMutationResolver(_factory);
        var materialized = await AddLinkAsync(PlaylistLinkMode.Materialized, "backend-playlist");

        var route = await resolver.ResolveAsync(Context(), ProtocolId(materialized));

        Assert.NotNull(route);
        Assert.True(route.Writable);
        Assert.Equal("backend-playlist", route.TargetPlaylistId);
        Assert.Null(await resolver.ResolveAsync(Context(userId: _otherUserId), ProtocolId(materialized)));
        Assert.Null(await resolver.ResolveAsync(Context(backend: "other-backend"), ProtocolId(materialized)));
        Assert.Null(await resolver.ResolveAsync(Context(library: "other-library"), ProtocolId(materialized)));
        Assert.Null(await resolver.ResolveAsync(
            Context(tenantId: Guid.CreateVersion7()), ProtocolId(materialized)));
    }

    [Theory]
    [InlineData(PlaylistLinkMode.Virtual, "backend-playlist")]
    [InlineData(PlaylistLinkMode.Hybrid, null)]
    [InlineData(PlaylistLinkMode.Materialized, "")]
    public async Task Resolver_LeavesPureVirtualOrUnmaterializedLinksReadOnly(
        PlaylistLinkMode mode,
        string? targetPlaylistId)
    {
        var resolver = new SubsonicPlaylistMutationResolver(_factory);
        var linkId = await AddLinkAsync(mode, targetPlaylistId);

        var route = await resolver.ResolveAsync(Context(), ProtocolId(linkId));

        Assert.NotNull(route);
        Assert.False(route.Writable);
        Assert.Null(route.TargetPlaylistId);
    }

    [Fact]
    public async Task Resolver_RejectsNonSubsonicTargetProtocol()
    {
        var resolver = new SubsonicPlaylistMutationResolver(_factory);
        var linkId = await AddLinkAsync(
            PlaylistLinkMode.Materialized,
            "backend-playlist",
            targetProtocol: "jellyfin");

        Assert.Null(await resolver.ResolveAsync(Context(), ProtocolId(linkId)));
    }

    [Fact]
    public async Task Resolver_RejectsDisabledSubsonicLink()
    {
        var resolver = new SubsonicPlaylistMutationResolver(_factory);
        var linkId = await AddLinkAsync(
            PlaylistLinkMode.Hybrid,
            "backend-playlist",
            enabled: false);

        Assert.Null(await resolver.ResolveAsync(Context(), ProtocolId(linkId)));
    }

    [Fact]
    public async Task JellyfinResolver_ReturnsOnlyExactScopedEnabledWritableTarget()
    {
        var resolver = new JellyfinPlaylistMutationResolver(_factory);
        var linkId = await AddLinkAsync(
            PlaylistLinkMode.Hybrid,
            " backend-playlist ",
            targetProtocol: "jellyfin");

        var route = await resolver.ResolveAsync(
            Context(protocol: ProtocolKind.Jellyfin),
            ProtocolId(linkId));

        Assert.NotNull(route);
        Assert.True(route.Writable);
        Assert.Equal("backend-playlist", route.TargetPlaylistId);
        Assert.Null(await resolver.ResolveAsync(
            Context(protocol: ProtocolKind.Jellyfin, userId: _otherUserId),
            ProtocolId(linkId)));
        Assert.Null(await resolver.ResolveAsync(
            Context(protocol: ProtocolKind.Jellyfin, backend: "other-backend"),
            ProtocolId(linkId)));
        Assert.Null(await resolver.ResolveAsync(
            Context(protocol: ProtocolKind.Jellyfin, library: "other-library"),
            ProtocolId(linkId)));
    }

    [Theory]
    [InlineData(PlaylistLinkMode.Virtual, "backend-playlist", true)]
    [InlineData(PlaylistLinkMode.Hybrid, null, true)]
    [InlineData(PlaylistLinkMode.Hybrid, "backend-playlist", false)]
    public async Task JellyfinResolver_LeavesVirtualUnmaterializedOrDisabledLinksUnavailable(
        PlaylistLinkMode mode,
        string? targetPlaylistId,
        bool enabled)
    {
        var resolver = new JellyfinPlaylistMutationResolver(_factory);
        var linkId = await AddLinkAsync(
            mode,
            targetPlaylistId,
            targetProtocol: "jellyfin",
            enabled: enabled);

        var route = await resolver.ResolveAsync(
            Context(protocol: ProtocolKind.Jellyfin),
            ProtocolId(linkId));

        if (!enabled)
        {
            Assert.Null(route);
            return;
        }

        Assert.NotNull(route);
        Assert.False(route.Writable);
        Assert.Null(route.TargetPlaylistId);
    }

    private async Task<Guid> AddLinkAsync(
        PlaylistLinkMode mode,
        string? targetPlaylistId,
        string targetProtocol = "navidrome",
        bool enabled = true)
    {
        var id = Guid.CreateVersion7();
        await using var db = await _factory.CreateDbContextAsync();
        db.PlaylistLinks.Add(new PlaylistLinkRecord
        {
            Id = id,
            TenantId = _tenantId,
            OwnerUserId = _ownerId,
            ProviderAccountId = _accountId,
            LibraryScopeId = "music",
            SourceProviderId = "spotify",
            SourcePlaylistId = $"source-{id:N}",
            SourcePlaylistIdHash = Hash(id.ToString("N")),
            TargetProtocol = targetProtocol,
            TargetBackendInstanceId = "backend",
            TargetPlaylistId = targetPlaylistId,
            Enabled = enabled,
            Mode = mode,
            MaterializationMode = PlaylistMaterializationMode.Reconcile,
            PreserveManualEntries = true,
            SyncName = true,
            SyncDescription = true,
            SyncArtwork = true,
            RuleVersion = "rules-v1",
            PolicyVersion = "policy-v1",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return id;
    }

    private ProtocolExecutionContext Context(
        Guid? tenantId = null,
        Guid? userId = null,
        string backend = "backend",
        string library = "music",
        ProtocolKind protocol = ProtocolKind.Subsonic)
    {
        var tenant = tenantId ?? _tenantId;
        var user = userId ?? _ownerId;
        return new ProtocolExecutionContext(
            protocol,
            backend,
            "principal",
            new AllstarrPrincipal(
                tenant,
                user,
                protocol.ToString().ToLowerInvariant(),
                backend,
                "principal",
                "Fixture user",
                false),
            "correlation",
            DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None,
            libraryScopeId: library);
    }

    private PlatformUserRecord User(Guid id, string name, DateTimeOffset now) => new()
    {
        Id = id,
        TenantId = _tenantId,
        DisplayName = name,
        Status = PlatformUserStatus.Active,
        CreatedAt = now,
        UpdatedAt = now
    };

    private static string ProtocolId(Guid linkId) =>
        PlaylistVirtualizationService.CreateProtocolId(linkId);

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public async Task DisposeAsync() => await _database.DisposeAsync();

    private sealed class Factory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);

        public Task<AllstarrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
