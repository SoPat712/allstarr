using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class RecordingKeptFilesTests
{
    [Theory]
    [InlineData("current", true)]
    [InlineData("foreign-owner", false)]
    [InlineData("changed-revision", false)]
    [InlineData("missing-proof", false)]
    [InlineData("malformed-proof", false)]
    public async Task RetainedOriginal_KeepRequiresCurrentRepresentationProofAndAccountOwnership(string change, bool accepted)
    {
        var path = await SeedRetainedOriginalKeepAsync(change);
        var result = await KeepController(linked: true).PromoteCachedDownload("cached.flac");
        if (accepted) Assert.IsType<AcceptedResult>(result);
        else
        {
            var status = Assert.IsAssignableFrom<IStatusCodeActionResult>(result).StatusCode;
            Assert.True(status is 403 or 409, $"Expected a safe source rejection, received {status}.");
        }
        await using var db = new AllstarrDbContext(database.Options);
        Assert.Equal(accepted ? 1 : 0, await db.Jobs.CountAsync(item => item.Type == RecordingKeptFiles.ExplicitKeepJobType && item.State == DurableJobState.Pending));
        Assert.Equal(audio, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedOriginal_TransactionalKeepAcceptanceRechecksTheSavedSourceRevision(bool changedRevision)
    {
        await SeedRetainedOriginalKeepAsync(changedRevision ? "changed-revision" : "current");
        CachedKeepSource proof;
        await using (var db = new AllstarrDbContext(database.Options))
        {
            var mapping = await db.DownloadedSongMappings.SingleAsync();
            proof = new(mapping.Id, mapping.Revision);
        }
        var failure = await Record.ExceptionAsync(() => ExplicitKeepService().RequestKeepAsync(KeepContext(owner),
            new("deezer", ProviderResourceKind.Track, "source-track"), new("Title", ["Artist"]), Guid.CreateVersion7(), cachedSource: proof));
        if (changedRevision) Assert.True(failure is InvalidOperationException or UnauthorizedAccessException);
        else Assert.Null(failure);
        await using var verify = new AllstarrDbContext(database.Options);
        Assert.Equal(changedRevision ? 0 : 1,
            await verify.Jobs.CountAsync(item => item.Type == RecordingKeptFiles.ExplicitKeepJobType && item.State == DurableJobState.Pending));
    }

    private async Task<string> SeedRetainedOriginalKeepAsync(string change)
    {
        var path = await SeedKeepCacheAsync();
        await using var db = new AllstarrDbContext(database.Options);
        var mapping = await db.DownloadedSongMappings.SingleAsync();
        mapping.ScopeKey = new DownloadedSongMappingScope(account, ProviderAudioQuality.Any, DownloadedSongRepresentation.Original, 7).Key;
        mapping.CacheIdentityJson = change switch
        {
            "missing-proof" => null,
            "malformed-proof" => "not-json",
            _ => JsonSerializer.Serialize(new
            {
                Device = "fixture-device",
                File = "fixture-file",
                Length = audio.Length,
                Sha256 = new string('a', 64),
                LastUsed = DateTimeOffset.UtcNow,
                Representation = "Original",
                SourceAccountRevision = 7
            })
        };
        if (change == "foreign-owner") (await db.ProviderAccounts.SingleAsync()).OwnerUserId = other;
        if (change == "changed-revision") (await db.ProviderAccounts.SingleAsync()).Revision = 8;
        await db.SaveChangesAsync();
        return path;
    }
}
