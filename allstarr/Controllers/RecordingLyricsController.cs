using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Models.Lyrics;
using allstarr.Filters;
using allstarr.Services.Admin;
using allstarr.Services.Jellyfin;
using allstarr.Services.Subsonic;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/recordings/{recordingId:guid}/lyrics")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class RecordingLyricsController(
    IRecordingLyricsReview review,
    AdminProtocolExecutionContextFactory contexts,
    JellyfinProxyService? jellyfin = null,
    SubsonicProxyService? subsonic = null) : ControllerBase
{
    [HttpGet]
    public Task<IActionResult> Get(Guid recordingId, CancellationToken cancellationToken, [FromQuery] string scope = "personal") =>
        ExecuteAsync(cancellationToken, (session, context) => review.GetAsync(context, recordingId, scope,
            (id, token) => ReadBackendAsync(session, id, token)));

    [HttpPut]
    public Task<IActionResult> Choose(Guid recordingId, [FromBody] ChooseRecordingLyricsRequest request, CancellationToken cancellationToken) =>
        ExecuteAsync(cancellationToken, (session, context) => review.ChooseAsync(context, recordingId,
            request.SourceId, request.Scope, request.ExpectedRevision, request.PreviewRevision,
            (id, token) => ReadBackendAsync(session, id, token)));

    [HttpDelete]
    public Task<IActionResult> Clear(Guid recordingId, [FromBody] ClearRecordingLyricsRequest request, CancellationToken cancellationToken) =>
        ExecuteAsync(cancellationToken, (_, context) => review.ClearAsync(context, recordingId, request.Scope, request.ExpectedRevision));

    private async Task<IActionResult> ExecuteAsync<T>(CancellationToken token,
        Func<AdminAuthSession, ProtocolExecutionContext, Task<T>> action)
    {
        if (HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] is not AdminAuthSession session)
            return Unauthorized();
        if (session.AllstarrUserId == null)
            return StatusCode(403, new { error = "A linked backend user is required." });
        try
        {
            token.ThrowIfCancellationRequested();
            var context = await contexts.CreateAsync(session, HttpContext.TraceIdentifier, token);
            return Ok(await action(session, context));
        }
        catch (UnauthorizedAccessException) { return StatusCode(403, new { error = "Your current account cannot change these lyrics." }); }
        catch (KeyNotFoundException) { return NotFound(new { error = "This recording is unavailable. Refresh the song and try again." }); }
        catch (LyricsChoiceConflictException) { return Conflict(new { error = "The lyrics or saved choice changed. Reload the sources and try again." }); }
        catch (ArgumentException) { return BadRequest(new { error = "The lyrics choice is invalid." }); }
        catch (HttpRequestException) { return StatusCode(503, new { error = "Lyrics sources are temporarily unavailable. Try again shortly." }); }
    }

    private async Task<LyricsInfo?> ReadBackendAsync(AdminAuthSession session, string itemId, CancellationToken token)
    {
        if (session.BackendType.Equals("jellyfin", StringComparison.OrdinalIgnoreCase))
        {
            if (jellyfin == null || string.IsNullOrWhiteSpace(session.JellyfinAccessToken)) return null;
            var headers = new HeaderDictionary { ["X-Emby-Token"] = session.JellyfinAccessToken };
            var (body, status) = await jellyfin.GetJsonAsync($"Audio/{Uri.EscapeDataString(itemId)}/Lyrics", null, headers, token);
            using (body)
                return status == 200 && body != null ? BackendLyrics.ParseJellyfin(body.RootElement) : null;
        }
        var authentication = session.SubsonicReadAuthentication;
        if (subsonic == null || authentication == null ||
            !authentication.TryGetValue("u", out var user) || !string.Equals(user, session.UserId, StringComparison.Ordinal))
            return null;
        var parameters = new Dictionary<string, string>(authentication, StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = itemId,
            ["f"] = "json",
            ["v"] = "1.16.1",
            ["c"] = "allstarr"
        };
        var response = await subsonic.RelayRawAsync("rest/getLyricsBySongId", SubsonicRequestParameters.FromDictionary(parameters), token);
        return response.IsSuccessStatusCode ? BackendLyrics.ParseSubsonic(response.Body, json: true) : null;
    }
}

public sealed record ChooseRecordingLyricsRequest(string SourceId, string Scope, LyricsChoiceRevision ExpectedRevision, string PreviewRevision);
public sealed record ClearRecordingLyricsRequest(string Scope, LyricsChoiceRevision ExpectedRevision);
