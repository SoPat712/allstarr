using System.Security.Cryptography;
using allstarr.Core.Downloads;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Music;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class RecordingKeptFilesTests
{
    [MediaToolsTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeptPublicationRejectsIncompleteOrShortAudioEvenWithAMatchingArtifactHash(bool incomplete)
    {
        var bytes = PlaybackAudioFixtures.Wave();
        if (incomplete) bytes = bytes[..300000];
        await File.WriteAllBytesAsync(artifact.SourcePath, bytes);
        await using (var edit = new AllstarrDbContext(database.Options))
        {
            var source = await edit.Set<ProviderDownloadArtifactEntity>().SingleAsync();
            source.Length = bytes.Length;
            source.ContentSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            source.MimeType = "audio/wav";
            source.Container = "wav";
            source.Codec = "pcm_s16le";
            await edit.SaveChangesAsync();
        }
        var request = Request() with { Metadata = new KeptRecordingMetadata("Fixture", ["Artist"], DurationMilliseconds: incomplete ? 8000 : 16000) };
        await Assert.ThrowsAsync<IOException>(() => Service(new ProviderAudioVerifier()).PublishAsync(request));
        await using var verify = new AllstarrDbContext(database.Options);
        Assert.Empty(await verify.Set<RecordingKeptRouteEntity>().ToArrayAsync());
        Assert.Empty(await verify.ManagedFiles.ToArrayAsync());
        Assert.False(Directory.Exists(placement.RootPath));
    }
}
