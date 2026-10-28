using System.Security.Cryptography;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using Moq;

namespace allstarr.Tests;

public sealed class ProviderDownloadArtifactAudioIntegrityTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("allstarr-download-integrity-").FullName;

    [MediaToolsTheory]
    [InlineData("wav")]
    [InlineData("flac")]
    public async Task DurableArtifactsRequireCompleteAudioBeforeBecomingVerified(string codec)
    {
        var store = Store(out var workspaces, out var published);
        var resolver = new ProviderDownloadArtifactResolver(store.Object, new() { RootPath = root });
        var complete = await AudioAsync(codec);
        foreach (var truncated in new[] { true, false })
        {
            var workspace = await resolver.CreateWorkspaceAsync(new(Guid.NewGuid(), Guid.NewGuid(), "fixture", null, Guid.NewGuid().ToString("N")));
            var bytes = truncated ? complete[..(complete.Length * 3 / 4)] : complete;
            var output = await WriteAsync(resolver, workspace.Reference, workspaces[workspace.Reference.WorkspaceId].DurableJobId, codec, bytes);

            if (truncated)
            {
                await Assert.ThrowsAsync<IOException>(() => resolver.ResolveAsync(workspace.Reference, output));
                Assert.Empty(published);
                store.Verify(item => item.AddVerifiedAsync(It.IsAny<ProviderDownloadArtifactEntity>(), It.IsAny<CancellationToken>()), Times.Never);
            }
            else
            {
                var artifact = await resolver.ResolveAsync(workspace.Reference, output);
                Assert.Equal(ProviderDownloadArtifactState.Verified, artifact.State);
                Assert.Equal(codec, artifact.Container);
                Assert.Equal(codec == "wav" ? "pcm_s16le" : "flac", artifact.Codec);
                Assert.Equal(bytes.Length, artifact.Length);
                Assert.Single(published);
            }
        }
    }

    [MediaToolsTheory]
    [InlineData("wav")]
    [InlineData("flac")]
    public async Task TransientArtifactsRejectSelfConsistentTruncationAndAcceptCompleteAudio(string codec)
    {
        var store = Store(out _, out var published);
        var resolver = new ProviderDownloadArtifactResolver(store.Object, new() { RootPath = root });
        var complete = await AudioAsync(codec);
        foreach (var truncated in new[] { true, false })
        {
            var job = Guid.NewGuid();
            var workspace = resolver.CreateTransientWorkspace(new(Guid.NewGuid(), job, "fixture", null, Guid.NewGuid().ToString("N")));
            var bytes = truncated ? complete[..(complete.Length * 3 / 4)] : complete;
            var output = await WriteAsync(resolver, workspace.Reference, job, codec, bytes);

            if (truncated) await Assert.ThrowsAsync<IOException>(() => resolver.ResolveTransientAsync(workspace, output));
            else
            {
                var artifact = await resolver.ResolveTransientAsync(workspace, output);
                Assert.Equal(codec, artifact.Media.Container);
                Assert.Equal(bytes.Length, artifact.Length);
            }
            resolver.DeleteTransientWorkspace(workspace);
        }
        Assert.Empty(published);
        store.Verify(item => item.AddVerifiedAsync(It.IsAny<ProviderDownloadArtifactEntity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [MediaToolsFact]
    public async Task CompleteMeasuredWaveRemainsValidWhenProviderLabelsItFlac()
    {
        var store = Store(out var workspaces, out _);
        var resolver = new ProviderDownloadArtifactResolver(store.Object, new() { RootPath = root });
        var workspace = await resolver.CreateWorkspaceAsync(new(Guid.NewGuid(), Guid.NewGuid(), "fixture", null, "actual-wave"));
        var output = await WriteAsync(resolver, workspace.Reference, workspaces[workspace.Reference.WorkspaceId].DurableJobId,
            "flac", PlaybackAudioFixtures.Wave(2));

        var artifact = await resolver.ResolveAsync(workspace.Reference, output);

        Assert.Equal("wav", artifact.Container);
        Assert.Equal("pcm_s16le", artifact.Codec);
    }

    private async Task<byte[]> AudioAsync(string codec)
    {
        var wave = PlaybackAudioFixtures.Wave(4);
        if (codec == "wav") return wave;
        var source = Path.Combine(root, Guid.NewGuid().ToString("N") + ".wav");
        var output = Path.Combine(root, Guid.NewGuid().ToString("N") + ".flac");
        await File.WriteAllBytesAsync(source, wave);
        await ExtensionDownloadDecryptionTests.RunAsync("ffmpeg",
            ["-v", "error", "-nostdin", "-y", "-threads", "1", "-filter_threads", "1", "-i", source, "-threads", "1", "-c:a", "flac", output]);
        return await File.ReadAllBytesAsync(output);
    }

    private static async Task<ProviderDownloadedArtifact> WriteAsync(ProviderDownloadArtifactResolver resolver,
        ProviderManagedWorkspaceReference workspace, Guid job, string codec, byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        var written = await resolver.WriteAsync(new(workspace, job, "fixture", "audio." + codec, input, 4 * 1024 * 1024)
        { ExpectedBytes = bytes.Length });
        Assert.Equal(bytes.Length, written.SizeBytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), written.Sha256);
        return new(written.ArtifactId, written.Sha256, written.SizeBytes,
            new(codec == "wav" ? "audio/wav" : "audio/flac", codec, codec == "wav" ? "pcm_s16le" : "flac"), true);
    }

    private static Mock<IProviderDownloadArtifactStore> Store(out Dictionary<string, ProviderDownloadWorkspaceEntity> workspaces,
        out List<ProviderDownloadArtifactEntity> published)
    {
        var rows = new Dictionary<string, ProviderDownloadWorkspaceEntity>(StringComparer.Ordinal);
        var verified = new List<ProviderDownloadArtifactEntity>();
        var store = new Mock<IProviderDownloadArtifactStore>(MockBehavior.Strict);
        store.Setup(item => item.CreateWorkspaceAsync(It.IsAny<ProviderDownloadWorkspaceEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderDownloadWorkspaceEntity value, CancellationToken _) => { rows[value.WorkspaceId] = value; return value; });
        store.Setup(item => item.GetWorkspaceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => rows.GetValueOrDefault(id));
        store.Setup(item => item.AddVerifiedAsync(It.IsAny<ProviderDownloadArtifactEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderDownloadArtifactEntity value, CancellationToken _) => { verified.Add(value); return value; });
        workspaces = rows;
        published = verified;
        return store;
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
