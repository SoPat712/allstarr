using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Intelligence;

namespace allstarr.Tests;

public sealed class ListeningHistoryImportArtifactStoreTests
{
    [Fact]
    public async Task StageVerifyAndDeleteProtectThePreviewedArtifact()
    {
        var root = Path.Combine(Path.GetTempPath(), $"allstarr-history-import-{Guid.NewGuid():N}");
        var options = new ListeningHistoryImportOptions { RootPath = root, MaximumUploadBytes = 1024 };
        var store = new ListeningHistoryImportArtifactStore(options);
        var importId = Guid.NewGuid();
        var content = Encoding.UTF8.GetBytes("[{\"ts\":\"2026-07-01T12:00:00Z\"}]");
        try
        {
            await using var source = new MemoryStream(content);
            var artifact = await store.StageAsync(importId, source, content.Length, CancellationToken.None);

            Assert.Equal(content.Length, artifact.SizeBytes);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(), artifact.ContentSha256);
            await store.VerifyAsync(importId, artifact.ContentSha256, artifact.SizeBytes, CancellationToken.None);

            await File.WriteAllBytesAsync(Path.Combine(root, $"{importId:N}.json"), [.. content, 0], CancellationToken.None);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.VerifyAsync(importId, artifact.ContentSha256, artifact.SizeBytes, CancellationToken.None));

            store.Delete(importId);
            Assert.False(File.Exists(Path.Combine(root, $"{importId:N}.json")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
