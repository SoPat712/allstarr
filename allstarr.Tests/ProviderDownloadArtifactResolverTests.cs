using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;

namespace allstarr.Tests;

public sealed class ProviderDownloadArtifactResolverTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "allstarr-provider-artifacts", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Resolve_VerifiesWorkspaceContainedFileAndPersistsCompleteLineage()
    {
        var store = new MemoryStore();
        var resolver = Resolver(store);
        var request = Request();
        var workspace = await resolver.CreateWorkspaceAsync(request);
        var content = Encoding.UTF8.GetBytes("verified audio");
        var path = Write(workspace.Reference, "provider/output.flac", content);

        var result = await resolver.ResolveAsync(workspace.Reference, Output("provider/output.flac", content));

        Assert.Equal(Path.GetFullPath(path), result.SourcePath);
        Assert.Equal(request.TenantId, result.TenantId);
        Assert.Equal(request.OwnerUserId, result.OwnerUserId);
        Assert.Equal(request.DurableJobId, result.DurableJobId);
        Assert.Equal(request.ProviderId, result.ProviderId);
        Assert.Equal(request.ProviderAccountId, result.ProviderAccountId);
        Assert.Equal(ProviderDownloadArtifactState.Verified, result.State);
        Assert.Equal("audio/flac", result.MimeType);
        Assert.Equal("flac", result.Container);
        Assert.Equal("flac", result.Codec);
        Assert.Equal(1_411_000, result.Bitrate);
        Assert.Equal(44_100, result.SampleRate);
        Assert.Equal(16, result.BitDepth);
        Assert.Equal(2, result.Channels);
        Assert.DoesNotContain(result.SourcePath, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateWorkspace_IsIdempotentForSameLineageAndKey()
    {
        var store = new MemoryStore();
        var resolver = Resolver(store);
        var request = Request();

        var first = await resolver.CreateWorkspaceAsync(request);
        var second = await resolver.CreateWorkspaceAsync(request);

        Assert.Equal(first.RecordId, second.RecordId);
        Assert.Equal(first.Reference.WorkspaceId, second.Reference.WorkspaceId);
        Assert.Single(store.Workspaces);
    }

    [Theory]
    [InlineData("../outside.flac")]
    [InlineData("/tmp/outside.flac")]
    [InlineData("nested/../../outside.flac")]
    public async Task Resolve_RejectsTraversalAndRootedArtifactReferences(string artifactId)
    {
        var store = new MemoryStore();
        var resolver = Resolver(store);
        var workspace = await resolver.CreateWorkspaceAsync(Request());
        var bytes = Encoding.UTF8.GetBytes("audio");
        await Assert.ThrowsAnyAsync<Exception>(() => resolver.ResolveAsync(workspace.Reference, Output(artifactId, bytes)));
        Assert.Empty(store.Artifacts);
    }

    [Fact]
    public async Task Resolve_RejectsChecksumOrLengthMismatchBeforePersistence()
    {
        var store = new MemoryStore();
        var resolver = Resolver(store);
        var workspace = await resolver.CreateWorkspaceAsync(Request());
        var bytes = Encoding.UTF8.GetBytes("real audio");
        Write(workspace.Reference, "track.flac", bytes);
        var wrong = new ProviderDownloadedArtifact("track.flac", new string('a', 64), bytes.Length,
            new ProviderMediaFormat("audio/flac", "flac", "flac"), true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(workspace.Reference, wrong));
        Assert.Empty(store.Artifacts);
    }

    [Fact]
    public async Task Resolve_RejectsSymlinkArtifact()
    {
        if (OperatingSystem.IsWindows()) return;
        var store = new MemoryStore();
        var resolver = Resolver(store);
        var workspace = await resolver.CreateWorkspaceAsync(Request());
        var outside = Path.Combine(root, "outside.flac");
        await File.WriteAllTextAsync(outside, "outside");
        var link = Path.Combine(root, workspace.Reference.WorkspaceId, "track.flac");
        File.CreateSymbolicLink(link, outside);
        var bytes = await File.ReadAllBytesAsync(outside);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => resolver.ResolveAsync(workspace.Reference, Output("track.flac", bytes)));
        Assert.Empty(store.Artifacts);
    }

    [Fact]
    public async Task Resolve_ReusesIdenticalArtifactAndRejectsArtifactIdContentChange()
    {
        var store = new MemoryStore();
        var resolver = Resolver(store);
        var workspace = await resolver.CreateWorkspaceAsync(Request());
        var bytes = Encoding.UTF8.GetBytes("same");
        Write(workspace.Reference, "track.flac", bytes);
        var first = await resolver.ResolveAsync(workspace.Reference, Output("track.flac", bytes));
        var second = await resolver.ResolveAsync(workspace.Reference, Output("track.flac", bytes));
        Assert.Equal(first.Id, second.Id);
        Assert.Single(store.Artifacts);
    }

    [Fact]
    public async Task FindByJob_RejectsArtifactChangedAfterVerification()
    {
        var store = new MemoryStore();
        var resolver = Resolver(store);
        var request = Request();
        var workspace = await resolver.CreateWorkspaceAsync(request);
        var verified = Encoding.UTF8.GetBytes("verified-audio");
        var path = Write(workspace.Reference, "track.flac", verified);
        await resolver.ResolveAsync(workspace.Reference, Output("track.flac", verified));
        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes("modified-audio"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.FindByJobAsync(request.TenantId, request.DurableJobId, request.ProviderId));

        Assert.Contains("content changed", exception.Message, StringComparison.Ordinal);
    }

    private ProviderDownloadArtifactResolver Resolver(MemoryStore store) => new(store, new() { RootPath = root });
    private ProviderDownloadWorkspaceRequest Request() => new(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "qobuz", Guid.CreateVersion7(), "favorite:event:download");
    private string Write(ProviderManagedWorkspaceReference workspace, string relative, byte[] content)
    { var path = Path.Combine(root, workspace.WorkspaceId, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, content); return path; }
    private static ProviderDownloadedArtifact Output(string id, byte[] bytes) => new(id,
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.Length,
        new ProviderMediaFormat("audio/flac", "flac", "flac", 1_411_000, 44_100, 16, 2), true);

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private sealed class MemoryStore : IProviderDownloadArtifactStore
    {
        public List<ProviderDownloadWorkspaceEntity> Workspaces { get; } = [];
        public List<ProviderDownloadArtifactEntity> Artifacts { get; } = [];
        public Task<ProviderDownloadWorkspaceEntity> CreateWorkspaceAsync(ProviderDownloadWorkspaceEntity value, CancellationToken token)
        { var existing = Workspaces.SingleOrDefault(item => item.WorkspaceId == value.WorkspaceId); if (existing is not null) return Task.FromResult(existing); Workspaces.Add(value); return Task.FromResult(value); }
        public Task<ProviderDownloadWorkspaceEntity?> GetWorkspaceAsync(string id, CancellationToken token) => Task.FromResult(Workspaces.SingleOrDefault(item => item.WorkspaceId == id));
        public Task<ProviderDownloadArtifactEntity> AddVerifiedAsync(ProviderDownloadArtifactEntity value, CancellationToken token)
        { var existing = Artifacts.SingleOrDefault(item => item.WorkspaceRecordId == value.WorkspaceRecordId && item.ProviderArtifactId == value.ProviderArtifactId); if (existing is not null) { if (existing.ContentSha256 != value.ContentSha256) throw new InvalidOperationException(); return Task.FromResult(existing); } Artifacts.Add(value); return Task.FromResult(value); }
        public Task<ProviderDownloadArtifactEntity?> FindByJobAsync(Guid tenantId, Guid jobId, string provider, CancellationToken token) => Task.FromResult(Artifacts.SingleOrDefault(item => item.TenantId == tenantId && item.DurableJobId == jobId && item.ProviderId == provider));
        public Task MarkPlacedAsync(Guid id, Guid managedId, CancellationToken token) { var item = Artifacts.Single(value => value.Id == id); item.State = ProviderDownloadArtifactState.Placed; item.ManagedFileId = managedId; return Task.CompletedTask; }
    }
}
