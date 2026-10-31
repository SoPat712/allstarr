using allstarr.Core.Music;
using allstarr.Core.Operations;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/mixes")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed partial class MixesController(PersonalMixService mixes, IDurableRuntimeSettings settings,
    IDbContextFactory<AllstarrDbContext> factory, IPlatformClock clock, MixCoverImageService covers) : ControllerBase
{
    [HttpGet]
    public Task<IActionResult> List([FromQuery] string? timeZoneId = null, CancellationToken token = default) => ExecuteAsync(async user =>
    {
        var enabled = (bool)(await settings.GetAsync(PersonalMixService.PublishKey, token)).Value;
        return (await mixes.ListAsync(user.Id, timeZoneId, token)).Select(mix => Card(mix, enabled));
    }, token);

    [HttpGet("{id:guid}")]
    public Task<IActionResult> Get(Guid id, CancellationToken token) => ExecuteAsync(async user =>
    {
        var mix = await mixes.GetAsync(user.Id, id, token);
        return new { mix = Card(mix, (bool)(await settings.GetAsync(PersonalMixService.PublishKey, token)).Value), songs = mix.Songs };
    }, token);

    [HttpGet("{id:guid}/cover")]
    public async Task<IActionResult> Cover(Guid id, CancellationToken token)
    {
        var result = await ExecuteAsync(async user =>
        {
            var mix = await mixes.GetAsync(user.Id, id, token);
            var input = MixCoverInput.From(mix);
            if (mix.Kind == "external")
            {
                await using var db = await factory.CreateDbContextAsync(token);
                var link = await db.PlaylistLinks.AsNoTracking().SingleAsync(item => item.Id == id && item.OwnerUserId == user.Id, token);
                input = input with { Source = link.SourceProviderId, SourcePlaylistId = link.SourcePlaylistId };
            }
            return covers.CreateResponse(input, HttpContext);
        }, token);
        return result is OkObjectResult { Value: IActionResult response } ? response : result;
    }

    [HttpPost("{id:guid}/refresh")]
    public Task<IActionResult> Refresh(Guid id, CancellationToken token) => ExecuteAsync(async user =>
        (await mixes.GenerateAsync(user.Id, id, token: token)).Single(item => item.Id == id), token);

    [HttpPost("{id:guid}/in-apps")]
    public Task<IActionResult> AddToApps(Guid id, CancellationToken token) => ExecuteAsync(async user =>
    {
        if (!(bool)(await settings.GetAsync(PersonalMixService.PublishKey, token)).Value) throw new UnauthorizedAccessException();
        await mixes.SetInAppsAsync(user.Id, id, true, token);
        return new { inApps = true };
    }, token);

    [HttpDelete("{id:guid}/in-apps")]
    public Task<IActionResult> RemoveFromApps(Guid id, CancellationToken token) => ExecuteAsync(async user =>
    {
        await mixes.SetInAppsAsync(user.Id, id, false, token);
        return new { inApps = false };
    }, token);

    [HttpPost("start")]
    public Task<IActionResult> Start(MixStartRequest request, CancellationToken token) => ExecuteAsync(async user =>
    {
        var preview = await mixes.StartAsync(user.Id, request, token);
        return new { previewId = preview.Id, preview.Songs, preview.Readiness };
    }, token);

    [HttpPost("start/{previewId:guid}/save")]
    public Task<IActionResult> Save(Guid previewId, MixSaveRequest request, CancellationToken token) => ExecuteAsync(async user =>
        await mixes.SaveCustomAsync(user.Id, previewId, request.Name, token), token);

    [HttpDelete("{id:guid}")]
    public Task<IActionResult> Delete(Guid id, CancellationToken token) => ExecuteAsync(async user =>
    {
        await mixes.DeleteCustomAsync(user.Id, id, token);
        return new { deleted = id };
    }, token);

    [HttpPost("{id:guid}/feedback")]
    public Task<IActionResult> Feedback(Guid id, MixFeedback request, CancellationToken token) => ExecuteAsync(async user =>
    {
        await mixes.FeedbackAsync(user.Id, id, request, token);
        return new { removed = request.SongId };
    }, token);

    [HttpGet("publish-setting")]
    public Task<IActionResult> PublishSetting(CancellationToken token) => ExecuteAsync(async _ =>
        new { enabled = (bool)(await settings.GetAsync(PersonalMixService.PublishKey, token)).Value, name = "Let listeners add mixes to their apps" }, token, true);

    [HttpPut("publish-setting")]
    public Task<IActionResult> SetPublishSetting(MixPublishSetting request, CancellationToken token) => ExecuteAsync(async user =>
        await settings.ApplyBatchAsync([new(PersonalMixService.PublishKey, request.Enabled ? "true" : "false")], "mixes", user.Id, token), token, true);

    [HttpPost("dry-run")]
    public Task<IActionResult> DryRun([FromQuery] Guid listenerId, [FromQuery] string? timeZoneId = null, CancellationToken token = default) =>
        ExecuteAsync(async _ => await mixes.GenerateAsync(listenerId, dryRun: true, timeZoneId: timeZoneId, token: token), token, true);

    private static object Card(PersonalMix mix, bool enabled, Guid? initialJobId = null) => new
    {
        mix.Id,
        mix.Kind,
        mix.Name,
        mix.Description,
        mix.Section,
        mix.SortOrder,
        mix.SourceLabel,
        mix.RecipeVersion,
        mix.UpdatedAt,
        mix.Readiness,
        inApps = mix.Added,
        appsAllowed = enabled,
        coverArtwork = mix.Songs.Take(4).Select(item => item.Song.Id).ToArray(),
        initialJobId
    };

    private async Task<IActionResult> ExecuteAsync(Func<UserRecord, Task<object>> action, CancellationToken token, bool administrator = false)
    {
        Response.Headers.CacheControl = "private, no-store";
        if (HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] is not AdminAuthSession session ||
            session.ExpiresAtUtc <= clock.UtcNow.UtcDateTime) return Unauthorized();
        await using var db = await factory.CreateDbContextAsync(token);
        var backend = session.BackendType.ToLowerInvariant();
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == session.AllstarrUserId && item.Enabled &&
            item.BackendType == backend && item.BackendInstanceId == session.BackendInstanceId &&
            item.BackendPrincipalId == session.UserId && item.IsAdmin == session.IsAdministrator, token);
        if (user == null || administrator && !user.IsAdmin) return StatusCode(403);
        try { return Ok(await action(user)); }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException) { return BadRequest(new { error = "The mix request is invalid." }); }
        catch (InvalidOperationException error) when (error.Message.StartsWith("Wait 15", StringComparison.Ordinal))
        { return StatusCode(429, new { error = error.Message }); }
    }
}

public sealed record MixPublishSetting(bool Enabled);

public sealed record MixSaveRequest(string Name);
