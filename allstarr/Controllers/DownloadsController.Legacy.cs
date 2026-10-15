using System.Text.Json;
using allstarr.Core.ManagedFiles;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Controllers;

public partial class DownloadsController
{
    private sealed record ImportedDownload(Core.ManagedFiles.ManagedFileOwnershipEntity File, LegacyMediaAdoptionRecord Adoption);

    private async Task<ImportedDownload[]> LoadImportedRecordsAsync(string storage, CancellationToken token, Guid? fileId = null)
    {
        var normalized = NormalizeStorage(storage);
        if (_contextFactory == null || normalized == "cache") return [];
        if (normalized is not ("kept" or "permanent" or "legacy")) throw new ArgumentException("Unsupported storage root");
        await using var db = await _contextFactory.CreateDbContextAsync(token);
        return await (from adoption in db.Set<LegacyMediaAdoptionRecord>().AsNoTracking()
                      join file in db.ManagedFiles.AsNoTracking() on adoption.ManagedFileId equals file.Id
                      where file.RemovedAt == null && (fileId == null || file.Id == fileId) && (normalized != "permanent" || !adoption.Kept) &&
                            (normalized != "legacy" || adoption.Kept)
                      select new ImportedDownload(file, adoption)).ToArrayAsync(token);
    }

    private async Task<ManagedDownloadFile[]> LoadImportedDownloadsAsync(string storage, CancellationToken token)
    {
        var records = await LoadImportedRecordsAsync(storage, token);
        if (records.Length == 0) return [];
        var placement = HttpContext.RequestServices.GetRequiredService<FilePlacementService>();
        var result = new List<ManagedDownloadFile>(records.Length);
        foreach (var item in records)
        {
            var record = EfManagedFileOwnershipStore.Map(item.File)!;
            try { placement.VerifyOwnedFileIdentity(record, token); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            var metadata = JsonSerializer.Deserialize<KeptRecordingMetadata>(item.Adoption.MetadataJson)!;
            var info = new FileInfo(record.CanonicalPath);
            result.Add(new($"imported/{record.Id:D}", "kept", metadata.Artists.FirstOrDefault() ?? "Unknown artist",
                metadata.Album ?? "", metadata.Title, info.Name, record.Length, AdminHelperService.FormatFileSize(record.Length),
                info.LastWriteTimeUtc, Path.GetExtension(info.Name).TrimStart('.').ToUpperInvariant(), null, null, null, null,
                metadata.DurationMilliseconds, "Original", item.Adoption.ProviderId, item.Adoption.ExternalId, null,
                info.LastAccessTimeUtc, null, "Imported", record.ReferenceCount, false, false));
        }
        return result.ToArray();
    }

    private async Task<IActionResult> DownloadImportedFileAsync(string path, string storage, CancellationToken token)
    {
        if (NormalizeStorage(storage) == "cache" || !Guid.TryParseExact(path["imported/".Length..], "D", out var id))
            return BadRequest(new { error = "Invalid imported file reference" });
        var item = (await LoadImportedRecordsAsync(storage, token, id)).SingleOrDefault(item => item.File.Id == id);
        if (item == null) return NotFound(new { error = "Imported file not found" });
        try
        {
            var record = EfManagedFileOwnershipStore.Map(item.File)!;
            var input = HttpContext.RequestServices.GetRequiredService<FilePlacementService>().OpenOwnedPlaybackRead(record, token);
            return File(input, "application/octet-stream", Path.GetFileName(record.CanonicalPath));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return Conflict(new { error = "The original file changed or is missing. Restore its mounted folder and retry." }); }
    }
}
