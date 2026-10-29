using allstarr.Core.Enrichment;
using allstarr.Core.Storage;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/recordings/{recordingId:guid}/tags")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class RecordingTagsController(SharedTagStore tags, IDbContextFactory<AllstarrDbContext> factory) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(Guid recordingId, CancellationToken token)
    {
        if (HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] is not AdminAuthSession session)
            return Unauthorized();
        await using var db = await factory.CreateDbContextAsync(token);
        var recording = await db.CanonicalRecordings.AsNoTracking().SingleOrDefaultAsync(item => item.Id == recordingId, token);
        if (recording == null) return NotFound();
        recordingId = recording.MergedIntoId ?? recordingId;
        if (!session.IsAdministrator && (session.AllstarrUserId is not { } owner ||
            recording.CreatedByUserId != owner && !await db.LibraryTracks.AnyAsync(item => item.OwnerUserId == owner &&
                item.CanonicalRecordingId == recordingId, token) && !await db.ExternalMetadataSnapshots.AnyAsync(item => item.OwnerUserId == owner &&
                db.ProviderTrackIdentities.Any(identity => identity.Id == item.ProviderTrackIdentityId && identity.CanonicalRecordingId == recordingId), token)))
            return StatusCode(StatusCodes.Status403Forbidden);
        return Ok(await tags.GetTags(recordingId, token));
    }
}
