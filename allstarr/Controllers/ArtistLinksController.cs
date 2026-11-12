using System.Text.Json.Serialization;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Providers.AppleMusicKit;
using allstarr.Filters;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/artist-links")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class ArtistLinksController(LibraryArtistReview review, LibraryArtistIndex index, LibraryArtistReviewJob jobs,
    AdminProtocolExecutionContextFactory contexts, IProviderRegistry registry) : ControllerBase
{
    public sealed record FixRequest
    {
        [JsonPropertyName("same")]
        public required bool Same { get; init; }
        [JsonPropertyName("scope")]
        public required string Scope { get; init; }
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string filter = "needs-look", [FromQuery] string? q = null,
        [FromQuery] string? cursor = null, [FromQuery] int limit = 50, CancellationToken cancellationToken = default)
    {
        if (!TrySession(out var session, out var error)) return error!;
        try
        {
            var context = await contexts.CreateAsync(session!, "artist-review", cancellationToken);
            return Ok(await review.ListAsync(context, filter, q, cursor, limit, cancellationToken));
        }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
    }

    [HttpGet("{libraryArtistId}")]
    public async Task<IActionResult> Detail(string libraryArtistId, CancellationToken cancellationToken)
    {
        if (!TrySession(out var session, out var error)) return error!;
        try
        {
            var context = await contexts.CreateAsync(session!, "artist-review", cancellationToken);
            return await review.DetailAsync(context, libraryArtistId, cancellationToken) is { } row ? Ok(row) : NotFound();
        }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
    }

    [HttpPut("{libraryArtistId}/{providerId}/{artistId}")]
    public Task<IActionResult> Put(string libraryArtistId, string providerId, string artistId, [FromBody] FixRequest request,
        CancellationToken cancellationToken) => SetAsync(libraryArtistId, providerId, artistId, request.Same, request.Scope, cancellationToken);

    [HttpDelete("{libraryArtistId}/{providerId}/{artistId}")]
    public Task<IActionResult> Delete(string libraryArtistId, string providerId, string artistId, [FromQuery] string scope,
        CancellationToken cancellationToken) => SetAsync(libraryArtistId, providerId, artistId, null, scope, cancellationToken);

    private async Task<IActionResult> SetAsync(string libraryArtistId, string providerId, string artistId, bool? same, string scope,
        CancellationToken token)
    {
        if (!TrySession(out var session, out var error)) return error!;
        string provider;
        try
        {
            provider = registry.ResolveProviderId(AppleMusicClient.NormalizeProviderId(providerId));
            if (!registry.TryGet(provider, out _)) return BadRequest();
        }
        catch (ArgumentException) { return BadRequest(); }
        if (scope is not ("household" or "you") ||
            string.IsNullOrWhiteSpace(artistId) || artistId.Length > 300 || artistId.Any(char.IsControl) || artistId.StartsWith("credit:", StringComparison.Ordinal))
            return BadRequest();
        try
        {
            var context = await contexts.CreateAsync(session!, "artist-review", token);
            var artist = await review.DetailAsync(context, libraryArtistId, token);
            if (artist == null) return NotFound();
            if (!await index.SetFixAsync(session!.AllstarrUserId!.Value, artist.Name, provider, artistId, same, scope == "household", token))
                return BadRequest();
            return Ok(await review.DetailAsync(context, libraryArtistId, token));
        }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
    }

    [HttpPost("review")]
    public async Task<IActionResult> Review(CancellationToken cancellationToken)
    {
        if (!TrySession(out var session, out var error)) return error!;
        try
        {
            await contexts.CreateAsync(session!, "artist-review", cancellationToken);
            var (queued, running) = await jobs.RequestAsync(session!.AllstarrUserId!.Value, cancellationToken);
            return Ok(new { queued, running });
        }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
    }

    private bool TrySession(out AdminAuthSession? session, out IActionResult? error)
    {
        session = null; error = null;
        if (HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] is not AdminAuthSession found)
        { error = Unauthorized(new { error = "Authentication required" }); return false; }
        if (!found.AllstarrUserId.HasValue || !found.IsAdministrator)
        { error = StatusCode(403); return false; }
        session = found; return true;
    }
}
