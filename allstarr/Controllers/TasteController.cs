using allstarr.Core.Enrichment;
using allstarr.Core.Music;
using allstarr.Core.Storage;
using allstarr.Filters;
using allstarr.Models.Domain;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/taste")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class TasteController(TasteService taste, IDbContextFactory<AllstarrDbContext> factory) : ControllerBase
{
    [HttpGet]
    public Task<IActionResult> Get(CancellationToken token) => ExecuteAsync(async user => await taste.GetAsync(user, token));

    [HttpPut("nudge")]
    public Task<IActionResult> PutNudge([FromBody] TasteNudgeRequest request, CancellationToken token) => ExecuteAsync(async user =>
    {
        await taste.PutNudgeAsync(user, request, token);
        return await taste.GetAsync(user, token);
    });

    [HttpDelete("nudge")]
    public Task<IActionResult> DeleteNudge([FromQuery] string kind, [FromQuery] string name, CancellationToken token) => ExecuteAsync(async user =>
    {
        await taste.DeleteNudgeAsync(user, kind, name, token);
        return await taste.GetAsync(user, token);
    });

    [HttpPost("pause")]
    public Task<IActionResult> Pause(CancellationToken token) => SetPaused(true, token);

    [HttpPost("resume")]
    public Task<IActionResult> Resume(CancellationToken token) => SetPaused(false, token);

    private Task<IActionResult> SetPaused(bool paused, CancellationToken token) => ExecuteAsync(async user =>
    {
        await taste.SetPausedAsync(user, paused, token);
        return await taste.GetAsync(user, token);
    });

    [HttpPost("reset")]
    public Task<IActionResult> Reset([FromBody] TasteResetRequest request, CancellationToken token) => ExecuteAsync(async user =>
    {
        await taste.ResetAsync(user, request.ClearNudges, token);
        return await taste.GetAsync(user, token);
    });

    [HttpGet("explain/{recordingId:guid}")]
    public Task<IActionResult> Explain(Guid recordingId, CancellationToken token) => ExecuteAsync(async user =>
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var artist = await db.LibraryTracks.Where(item => item.OwnerUserId == user && item.CanonicalRecordingId == recordingId)
            .Select(item => item.Artist).FirstOrDefaultAsync(token);
        if (artist == null)
        {
            var snapshot = await (from item in db.ExternalMetadataSnapshots
                                  join identity in db.ProviderTrackIdentities on item.ProviderTrackIdentityId equals identity.Id
                                  where item.OwnerUserId == user && identity.CanonicalRecordingId == recordingId
                                  orderby item.RetrievedAt descending
                                  select item).FirstOrDefaultAsync(token);
            if (snapshot != null) artist = SharedTagMetadata.Read(snapshot.ProviderId, snapshot.PayloadJson, snapshot.RetrievedAt).Artist;
        }
        return await taste.ScoreAsync(user, new Song { CanonicalRecordingId = recordingId, Artist = artist ?? "" }, token: token);
    });

    private async Task<IActionResult> ExecuteAsync(Func<Guid, Task<object>> action)
    {
        if (HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] is not AdminAuthSession session) return Unauthorized();
        if (session.AllstarrUserId is not { } user) return StatusCode(403, new { error = "A linked backend user is required." });
        try { return Ok(await action(user)); }
        catch (UnauthorizedAccessException) { return StatusCode(403, new { error = "This listener is unavailable." }); }
        catch (ArgumentException) { return BadRequest(new { error = "The taste request is invalid." }); }
    }
}

public sealed record TasteResetRequest(bool ClearNudges = false);
