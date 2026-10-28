using allstarr.Core.Downloads;
using Moq;

namespace allstarr.Tests;

public sealed class ProviderTryWorkspaceTests
{
    [Fact]
    public async Task TransientBudget_BoundsProviderWritesAndStartupOnlyRemovesTryWorkspaces()
    {
        var root = Path.Combine(Path.GetTempPath(), "try-workspace-tests", Guid.NewGuid().ToString("N"));
        var resolver = new ProviderDownloadArtifactResolver(Mock.Of<IProviderDownloadArtifactStore>(), new() { RootPath = root }, audioVerifier: FixtureAudioVerifier.Instance);
        var job = Guid.NewGuid();
        var workspace = resolver.CreateTransientWorkspace(new(null, job, "fixture", null, "provider-try-test")
        { MaximumBytes = 3 });
        var other = resolver.CreateTransientWorkspace(new(null, Guid.NewGuid(), "fixture", null, "stream-test"));
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => resolver.WriteAsync(new(workspace.Reference, job, "fixture", "track",
                new MemoryStream([1, 2, 3, 4]), 1024)));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, workspace.Reference.WorkspaceId)));
            await resolver.WriteAsync(new(workspace.Reference, job, "fixture", "first", new MemoryStream([1, 2]), 1024));
            await Assert.ThrowsAsync<InvalidDataException>(() => resolver.WriteAsync(new(workspace.Reference, job, "fixture", "second",
                new MemoryStream([3, 4]), 1024)));
            resolver.DeleteAbandonedTryWorkspaces();
            Assert.False(Directory.Exists(Path.Combine(root, workspace.Reference.WorkspaceId)));
            Assert.True(Directory.Exists(Path.Combine(root, other.Reference.WorkspaceId)));
        }
        finally
        {
            resolver.DeleteTransientWorkspace(workspace);
            resolver.DeleteTransientWorkspace(other);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
