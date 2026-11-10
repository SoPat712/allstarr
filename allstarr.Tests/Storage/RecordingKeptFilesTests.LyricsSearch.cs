using allstarr.Core.ManagedFiles;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace allstarr.Tests;

public sealed partial class RecordingKeptFilesTests
{
    [Fact]
    public async Task LyricsSearch_BatchAvailabilityReturnsRequestedAliasesForPublishedHouseholdFile()
    {
        await Service().PublishAsync(Request());
        var survivor = (await identities.CreateRecordingAsync(Actor(owner), "lyrics-search-survivor")).Recording.Id;
        await using (var db = new AllstarrDbContext(database.Options))
        {
            var recording = await db.CanonicalRecordings.SingleAsync(item => item.Id == recordingId);
            recording.MergedIntoId = survivor;
            recording.Revision++;
            await db.SaveChangesAsync();
        }

        var result = await Service().FindAvailableAsync(Actor(other), [recordingId, survivor, Guid.CreateVersion7()], default);

        Assert.Equal(2, result.Count);
        Assert.Contains(recordingId, result);
        Assert.Contains(survivor, result);
    }

    [Fact]
    public async Task LyricsSearch_BatchAvailabilityExcludesPrivateSourcesAndChangedFiles()
    {
        Assert.Empty(await Service().FindAvailableAsync(Actor(other), [recordingId], default));
        var published = await Service().PublishAsync(Request());
        await File.WriteAllBytesAsync(published.Path, new byte[checked((int)published.Length)]);
        Assert.Empty(await Service().FindAvailableAsync(Actor(other), [recordingId], default));
    }

    [Fact]
    public async Task LyricsSearch_BatchAvailabilityExcludesMissingFiles()
    {
        var published = await Service().PublishAsync(Request());
        File.Delete(published.Path);
        Assert.Empty(await Service().FindAvailableAsync(Actor(other), [recordingId], default));
    }

    [Fact]
    public async Task LyricsSearch_BatchAvailabilityUsesLegacyAdoptionWhenRouteIsAbsent()
    {
        var legacyRoot = Path.Combine(root, "legacy");
        Directory.CreateDirectory(legacyRoot);
        var path = Path.Combine(legacyRoot, "song.flac");
        await File.WriteAllBytesAsync(path, audio);
        await using var scope = services.CreateAsyncScope();
        var files = scope.ServiceProvider.GetRequiredService<FilePlacementService>();
        var identity = files.InspectLegacyFile(legacyRoot, path);
        var snapshot = new LegacyMediaFileSnapshot(Guid.NewGuid(), legacyRoot, path, audio.Length, identity.DeviceId,
            identity.FileId, File.GetLastWriteTimeUtc(path).Ticks, true, "deezer", "source-track", new("Song", ["Artist"]));
        var prepared = await files.PrepareLegacyAdoptionAsync(snapshot, DateTimeOffset.UtcNow, default);
        var file = await files.AdoptLegacyFileAsync(prepared, snapshot, $"legacy-original:{prepared.Id:N}", default);
        await using (var db = new AllstarrDbContext(database.Options))
        {
            (await db.Users.SingleAsync(item => item.Id == owner)).IsAdmin = true;
            await db.SaveChangesAsync();
            var audit = new AuditEventRecord { Id = Guid.CreateVersion7(), ActorUserId = owner };
            var import = new LegacyEnvImportRecord
            {
                Id = Guid.CreateVersion7(),
                SourceSha256 = new string('a', 64),
                ActorUserId = owner,
                AuditEventId = audit.Id,
                AppliedAt = DateTimeOffset.UtcNow
            };
            db.AddRange(audit, import, new LegacyMediaAdoptionRecord
            {
                Id = Guid.CreateVersion7(),
                ImportId = import.Id,
                ManagedFileId = file.Id,
                CanonicalRecordingId = recordingId,
                Kept = true,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var result = await Service().FindAvailableAsync(Actor(other), [recordingId], default);

        Assert.Contains(recordingId, result);
    }
}
