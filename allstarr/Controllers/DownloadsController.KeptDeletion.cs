using allstarr.Core.ManagedFiles;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

public sealed record KeptRecordingDeletePreviewRequest(string Path);

public partial class DownloadsController
{
    [HttpPost("downloads/kept/delete-preview")]
    public async Task<IActionResult> PreviewKeptDeletion([FromBody] KeptRecordingDeletePreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var execution = await RequireKeepContextAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(request.Path) || !IsSafeRequestedPath("kept", request.Path))
                return BadRequest(new { error = "Invalid kept file path." });
            if (!TryResolveExistingFile("kept", request.Path, out _, out var fullPath)) return NotFound();
            var ownership = await FindOwnershipAsync(fullPath, cancellationToken);
            if (ownership?.ManagedFileId is not { } fileId || !ownership.CanDeleteKeptCopy)
                return Conflict(new { error = "Only household kept copies can be deleted here." });
            return Ok(await _kept!.PreviewDeletionAsync(execution, fileId, cancellationToken));
        }
        catch (UnauthorizedAccessException) { return Unauthorized(new { error = "Current media-server administrator access is required." }); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException)
        { return Conflict(new { error = "The kept copy changed. Refresh storage and review a new deletion preview." }); }
    }

    [HttpPost("downloads/kept/delete")]
    public async Task<IActionResult> DeleteKeptCopy([FromBody] KeptRecordingDeleteRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var execution = await RequireKeepContextAsync(cancellationToken);
            var queued = await _kept!.RequestDeletionAsync(execution, request, cancellationToken);
            return Accepted(new { jobId = queued.JobId, queued = queued.Created });
        }
        catch (UnauthorizedAccessException) { return Unauthorized(new { error = "Current media-server administrator access is required." }); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException) { return BadRequest(new { error = "Confirm Delete kept copy using the current storage preview." }); }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        { return Conflict(new { error = "The kept copy or its references changed. Review a fresh deletion preview." }); }
    }
}
