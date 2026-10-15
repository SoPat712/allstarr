using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Jobs;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class ProviderDownloadArtifactBindingTests
{
    [Fact]
    public async Task Resolve_PersistsAndReloadsExactTrackAndAccountRevision()
    {
        await using var fixture = await BindingFixture.CreateAsync(accountBound: true);
        var (workspace, output) = await fixture.WriteArtifactAsync();
        var track = Track("spotify", "actual-track-42");

        var result = await fixture.Resolver.ResolveAsync(
            workspace.Reference, output, sourceTrack: track, sourceAccountRevision: 0);
        var fresh = await fixture.FreshResolver().FindByJobAsync(fixture.JobId, fixture.ProviderId);

        Assert.Equal(track.Value, result.SourceExternalTrackId);
        Assert.Equal(0L, result.SourceAccountRevision);
        Assert.NotNull(fresh);
        Assert.Equal(track.Value, fresh!.SourceExternalTrackId);
        Assert.Equal(0L, fresh.SourceAccountRevision);
    }

    [Fact]
    public async Task Resolve_PersistsAnonymousTrackBindingWithoutAccountRevision()
    {
        await using var fixture = await BindingFixture.CreateAsync(accountBound: false);
        var (workspace, output) = await fixture.WriteArtifactAsync();
        var track = Track(fixture.ProviderId, "anonymous-track");

        var result = await fixture.Resolver.ResolveAsync(
            workspace.Reference, output, sourceTrack: track, sourceAccountRevision: null);

        Assert.Equal(track.Value, result.SourceExternalTrackId);
        Assert.Null(result.SourceAccountRevision);
    }

    [Fact]
    public async Task Resolve_RejectsSourceFromAnotherProviderWithoutAcceptingArtifact()
    {
        await using var fixture = await BindingFixture.CreateAsync(accountBound: true);
        var (workspace, output) = await fixture.WriteArtifactAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Resolver.ResolveAsync(
            workspace.Reference, output, sourceTrack: Track("qobuz", "same-track"), sourceAccountRevision: 0));

        Assert.Null(await fixture.FreshResolver().FindByJobAsync(fixture.JobId, fixture.ProviderId));
    }

    [Fact]
    public async Task Resolve_RejectsNonTrackSourceWithoutAcceptingArtifact()
    {
        await using var fixture = await BindingFixture.CreateAsync(accountBound: true);
        var (workspace, output) = await fixture.WriteArtifactAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Resolver.ResolveAsync(
            workspace.Reference,
            output,
            sourceTrack: new ProviderExternalResourceId(fixture.ProviderId, ProviderResourceKind.Album, "album"),
            sourceAccountRevision: 0));

        Assert.Null(await fixture.FreshResolver().FindByJobAsync(fixture.JobId, fixture.ProviderId));
    }

    [Fact]
    public async Task Resolve_RejectsMissingOrNegativeAccountRevision()
    {
        await using var missingFixture = await BindingFixture.CreateAsync(accountBound: true);
        var (missingWorkspace, missingOutput) = await missingFixture.WriteArtifactAsync();
        var track = Track(missingFixture.ProviderId, "account-track");

        await Assert.ThrowsAnyAsync<Exception>(() => missingFixture.Resolver.ResolveAsync(
            missingWorkspace.Reference, missingOutput, sourceTrack: track, sourceAccountRevision: null));
        Assert.Null(await missingFixture.FreshResolver().FindByJobAsync(missingFixture.JobId, missingFixture.ProviderId));

        await using var negativeFixture = await BindingFixture.CreateAsync(accountBound: true);
        var (negativeWorkspace, negativeOutput) = await negativeFixture.WriteArtifactAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => negativeFixture.Resolver.ResolveAsync(
            negativeWorkspace.Reference, negativeOutput, sourceTrack: track, sourceAccountRevision: -1));
        Assert.Null(await negativeFixture.FreshResolver().FindByJobAsync(negativeFixture.JobId, negativeFixture.ProviderId));
    }

    [Fact]
    public async Task Resolve_RejectsBindingMismatchOnArtifactReuseIncludingUnbound()
    {
        await using var fixture = await BindingFixture.CreateAsync(accountBound: true);
        var (workspace, output) = await fixture.WriteArtifactAsync();

        var first = await fixture.Resolver.ResolveAsync(
            workspace.Reference, output, sourceTrack: Track(fixture.ProviderId, "first-track"), sourceAccountRevision: 0);
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Resolver.ResolveAsync(
            workspace.Reference, output, sourceTrack: Track(fixture.ProviderId, "second-track"), sourceAccountRevision: 0));
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Resolver.ResolveAsync(workspace.Reference, output));

        var current = await fixture.FreshResolver().FindByJobAsync(fixture.JobId, fixture.ProviderId);
        Assert.NotNull(current);
        Assert.Equal(first.SourceExternalTrackId, current!.SourceExternalTrackId);
        Assert.Equal(first.SourceAccountRevision, current.SourceAccountRevision);
    }

    [Fact]
    public async Task LegacyUnboundArtifactRemainsReadableAndSourcePathIsNotSerialized()
    {
        await using var fixture = await BindingFixture.CreateAsync(accountBound: false);
        var (workspace, output) = await fixture.WriteArtifactAsync();

        var result = await fixture.Resolver.ResolveAsync(workspace.Reference, output);
        var fresh = await fixture.FreshResolver().FindByJobAsync(fixture.JobId, fixture.ProviderId);

        Assert.Null(result.SourceExternalTrackId);
        Assert.Null(result.SourceAccountRevision);
        Assert.NotNull(fresh);
        Assert.Null(fresh!.SourceExternalTrackId);
        Assert.Null(fresh.SourceAccountRevision);
        Assert.DoesNotContain("SourcePath", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.DoesNotContain(result.SourcePath, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    private static ProviderExternalResourceId Track(string provider, string value) =>
        new(provider, ProviderResourceKind.Track, value);

    internal sealed class BindingFixture : IAsyncDisposable
    {
        private static readonly byte[] Content = Encoding.UTF8.GetBytes("safe fixture audio bytes");
        private readonly SqliteTestDatabase database;

        private BindingFixture(
            SqliteTestDatabase database,
            TestDbContextFactory factory,
            ProviderDownloadArtifactResolver resolver,
            string root,
            Guid userId,
            Guid jobId,
            Guid? accountId)
        {
            this.database = database;
            Factory = factory;
            Resolver = resolver;
            Root = root;
            UserId = userId;
            JobId = jobId;
            AccountId = accountId;
        }

        private const string Provider = "spotify";
        public string ProviderId => Provider;
        private TestDbContextFactory Factory { get; }
        public ProviderDownloadArtifactResolver Resolver { get; }
        private string Root { get; }
        public Guid UserId { get; }
        public Guid JobId { get; }
        public Guid? AccountId { get; }

        public static async Task<BindingFixture> CreateAsync(bool accountBound)
        {
            var database = await SqliteTestDatabase.CreateAsync();
            var factory = new TestDbContextFactory(database.Options);
            var userId = Guid.CreateVersion7();
            var jobId = Guid.CreateVersion7();
            Guid? accountId = accountBound ? Guid.CreateVersion7() : null;
            var now = DateTimeOffset.UtcNow;

            await using (var db = await factory.CreateDbContextAsync())
            {
                db.Users.Add(new UserRecord
                {
                    Id = userId,
                    BackendType = "subsonic",
                    BackendInstanceId = "primary",
                    BackendPrincipalId = userId.ToString("N"),
                    DisplayName = "Binding fixture",
                    Enabled = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                    LastSeenAt = now
                });
                var job = DatabaseLineageConstraintTests.Job(jobId, userId, "binding-job", now);
                if (accountId.HasValue)
                {
                    job.ProviderAccountId = accountId;
                    job.ProviderCapability = "download";
                }
                db.Jobs.Add(job);
                if (accountId.HasValue)
                {
                    db.ProviderAccounts.Add(new ProviderAccountRecord
                    {
                        Id = accountId.Value,
                        OwnerUserId = userId,
                        ProviderId = Provider,
                        DisplayName = "Binding account",
                        Enabled = true,
                        Revision = 0,
                        CreatedAt = now,
                        UpdatedAt = now
                    });
                }
                await db.SaveChangesAsync();
            }

            var root = Path.Combine(Path.GetTempPath(), "allstarr-provider-binding", Guid.NewGuid().ToString("N"));
            var resolver = new ProviderDownloadArtifactResolver(
                new EfProviderDownloadArtifactStore(factory),
                new ProviderDownloadWorkspaceOptions { RootPath = root });
            return new(database, factory, resolver, root, userId, jobId, accountId);
        }

        public async Task<(ProviderDownloadWorkspace Workspace, ProviderDownloadedArtifact Output)> WriteArtifactAsync()
        {
            var workspace = await Resolver.CreateWorkspaceAsync(new(
                UserId, JobId, ProviderId, AccountId, "binding-fixture"));
            await using var content = new MemoryStream(Content, writable: false);
            var write = await Resolver.WriteAsync(new(
                workspace.Reference,
                JobId,
                ProviderId,
                "track.flac",
                content,
                1024));
            return (workspace, new ProviderDownloadedArtifact(
                write.ArtifactId,
                write.Sha256,
                write.SizeBytes,
                new ProviderMediaFormat("audio/flac", "flac", "flac", 1_411_000, 44_100, 16, 2),
                true));
        }

        public ProviderDownloadArtifactResolver FreshResolver() => new(
            new EfProviderDownloadArtifactStore(Factory),
            new ProviderDownloadWorkspaceOptions { RootPath = Root });

        public async ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
            await database.DisposeAsync();
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<AllstarrDbContext> options)
        : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);

        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AllstarrDbContext(options));
    }
}
