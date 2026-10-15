using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Downloads;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class RecordingKeptFilesTests
{
    [Theory]
    [Trait("Lane", "ReleaseCritical")]
    [InlineData("legacy")]
    [InlineData("legacy-hash")]
    [InlineData("unrelated")]
    [InlineData("foreign-account")]
    [InlineData("reserved")]
    [InlineData("symlink")]
    public async Task DownloadRetryAfterUpgradeDiscardsOnlyItsOwnedLegacyPartialWorkspace(string kind)
    {
        if (kind == "reserved") await PlaceReservationAsync(ReservationKey());
        await using (var db = new AllstarrDbContext(database.Options))
        {
            db.ProviderDownloadArtifacts.Remove(await db.ProviderDownloadArtifacts.SingleAsync());
            await db.SaveChangesAsync();
        }
        var key = kind == "legacy-hash" ? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("upgrade-job"))) : "revision-retry";
        var legacy = await artifacts.CreateWorkspaceAsync(new(owner, jobId, "deezer", kind == "foreign-account" ? null : account,
            kind == "unrelated" ? "different-command" : key));
        var directory = Path.Combine(root, "workspaces", legacy.Reference.WorkspaceId);
        var partial = Path.Combine(directory, "unfinished.flac.part");
        await File.WriteAllTextAsync(partial, "partial bytes from before upgrade");
        var outside = Path.Combine(root, "unrelated-source.flac");
        await File.WriteAllTextAsync(outside, "unrelated bytes");
        if (kind == "symlink") File.CreateSymbolicLink(Path.Combine(directory, "outside-link"), outside);
        await using (var db = new AllstarrDbContext(database.Options))
            Assert.Empty(await db.ProviderDownloadArtifacts.ToArrayAsync());
        var capability = new RevisionDownloadCapability(artifacts);
        var command = RevisionCommand() with { Attempt = 1, IdempotencyKey = key };
        var download = RevisionDownloadService(capability, 7);

        if (kind == "symlink")
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => download.ExecuteAsync(command));
            Assert.Equal("unrelated bytes", await File.ReadAllTextAsync(outside));
            Assert.True(File.Exists(partial));
            Assert.Equal(0, capability.DownloadCalls);
            return;
        }
        var result = await download.ExecuteAsync(command);

        Assert.True(result.Succeeded, result.ErrorCode);
        var fresh = Assert.IsType<VerifiedProviderDownloadArtifact>(result.Artifact);
        Assert.Equal(1, capability.DownloadCalls);
        Assert.NotEqual(legacy.RecordId, fresh.WorkspaceRecordId);
        Assert.Equal(RevisionDownloadCapability.Bytes, await File.ReadAllBytesAsync(fresh.SourcePath));
        Assert.Equal(kind is "unrelated" or "foreign-account" or "reserved", Directory.Exists(directory));
        Assert.Equal("unrelated bytes", await File.ReadAllTextAsync(outside));
        Assert.Equal(audio, await File.ReadAllBytesAsync(artifact.SourcePath));
    }
}
