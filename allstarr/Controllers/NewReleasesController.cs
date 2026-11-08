using allstarr.Core.Capabilities;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Core.Releases;
using allstarr.Core.Settings;
using allstarr.Filters;
using allstarr.Services.Admin;
using allstarr.Services.Local;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

[ApiController]
[Route("api/admin/releases")]
[ServiceFilter(typeof(AdminPortFilter))]
public sealed class NewReleasesController(NewReleasesService releases, AdminProtocolExecutionContextFactory? protocolContexts = null,
    IProtocolProviderGateway? providers = null, PersonalMixService? mixes = null, RecordingKeptFiles? kept = null,
    IDurableRuntimeSettings? settings = null) : ControllerBase
{
    [HttpGet("artists")]
    public Task<IActionResult> List(CancellationToken cancellationToken) => ExecuteAsync(async owner =>
        Ok(await releases.ListAsync(owner, cancellationToken)));

    [HttpPut("artists")]
    public Task<IActionResult> Follow([FromBody] FollowReleaseArtistRequest request, CancellationToken cancellationToken) =>
        ExecuteAsync(async owner =>
        {
            await releases.FollowAsync(owner, new(request.ProviderId, request.ArtistId, request.DisplayName), cancellationToken);
            return NoContent();
        });

    [HttpDelete("artists/{providerId}/{artistId}")]
    public Task<IActionResult> Unfollow(string providerId, string artistId, CancellationToken cancellationToken) =>
        ExecuteAsync(async owner =>
        {
            await releases.UnfollowAsync(owner, providerId, artistId, cancellationToken);
            return NoContent();
        });

    [HttpPost("artists/from-hearts")]
    public Task<IActionResult> FollowHearted(CancellationToken cancellationToken) => ExecuteAsync(async owner =>
        Ok(new { followed = await releases.FollowHeartedArtistsAsync(owner, cancellationToken) }));

    [HttpGet("artists/search")]
    public Task<IActionResult> Search([FromQuery] string? query, CancellationToken cancellationToken) => ExecuteAsync(async owner =>
        Ok(await releases.SearchArtistsAsync(owner, query ?? "", cancellationToken)));

    [HttpPost("artists/from-history")]
    public Task<IActionResult> FollowFromHistory(CancellationToken cancellationToken) => ExecuteAsync(async owner =>
        Ok(new { followed = await releases.FollowTopArtistsAsync(owner, cancellationToken) }));

    [HttpGet]
    public Task<IActionResult> Inbox(CancellationToken cancellationToken) => ExecuteAsync(async owner =>
        Ok(await releases.ReadAsync(owner, cancellationToken)));

    // The songs on a release, from the first source that can list them.
    [HttpGet("{entryId}/tracks")]
    public Task<IActionResult> Tracks(string entryId, CancellationToken cancellationToken) => ExecuteAsync(async owner =>
    {
        var release = await ReleaseAlbumAsync(owner, entryId, cancellationToken);
        if (release.Failure is { } failure) return failure;
        if (release.Album == null) return Ok(new { providerId = (string?)null, tracks = Array.Empty<object>() });
        return Ok(new
        {
            providerId = release.ProviderId,
            tracks = release.Album.Songs.Select((song, index) => new
            {
                number = song.Track ?? index + 1,
                title = song.Title,
                artists = song.Artists.Count > 0 ? string.Join(", ", song.Artists) : song.Artist,
                durationMs = song.Duration * 1000L,
                isExplicit = song.ExplicitContentLyrics == 1
            }).ToArray()
        });
    });

    // Puts the release in the listener's apps as a mix with exactly its songs.
    [HttpPost("{entryId}/apps")]
    public Task<IActionResult> AddToApps(string entryId, CancellationToken cancellationToken) => ExecuteAsync(async owner =>
    {
        if (mixes == null || settings == null) return StatusCode(503, new { error = "Mixes are unavailable." });
        if (!(bool)(await settings.GetAsync(PersonalMixService.PublishKey, cancellationToken)).Value)
            return StatusCode(403, new { error = "Adding mixes to apps is turned off for this household." });
        var release = await ReleaseAlbumAsync(owner, entryId, cancellationToken);
        if (release.Failure is { } failure) return failure;
        if (release.Album == null) return Conflict(new { error = "No source lists this release's songs yet." });
        if (release.Album.Songs.Count > 100)
            return Conflict(new { error = "This release has more than 100 songs and cannot be added to apps." });
        var entry = release.Entry!;
        var type = entry.Type.ToLowerInvariant() switch { "single" => "Single", "ep" => "EP", "album" => "Album", _ => "Release" };
        return await SaveReleaseAsync(() => mixes.SaveListAsync(owner, ReleaseGuid(owner, entryId, "apps"), entry.Title,
            $"{entry.Artist} · {type}", release.Album.Songs, "From the release", cancellationToken));
    });

    internal static async Task<IActionResult> SaveReleaseAsync(Func<Task<PersonalMix>> save)
    {
        try
        {
            var mix = await save();
            return new OkObjectResult(new { mixId = mix.Id, songCount = mix.Songs.Count });
        }
        catch (ArgumentException exception) { return new ConflictObjectResult(new { error = exception.Message }); }
        catch (InvalidOperationException) { return new ConflictObjectResult(new { error = "Your apps changed while saving this release. Try again." }); }
    }

    // Keeps every song on the release, the same way Keep works for one song.
    [HttpPost("{entryId}/keep")]
    public Task<IActionResult> KeepAlbum(string entryId, CancellationToken cancellationToken) => ExecuteAsync(async owner =>
    {
        if (HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] is not AdminAuthSession { IsAdministrator: true })
            return StatusCode(403, new { error = "Administrator access is required to keep albums." });
        if (kept == null) return StatusCode(503, new { error = "Keeping songs is unavailable." });
        var release = await ReleaseAlbumAsync(owner, entryId, cancellationToken);
        if (release.Failure is { } failure) return failure;
        if (release.Album == null) return Conflict(new { error = "No source lists this release's songs yet." });
        int queued = 0, skipped = 0;
        foreach (var song in release.Album.Songs)
        {
            var source = LocalLibraryService.ParseExternalResource(song.Id);
            if (!source.isExternal || source.type != "song") { skipped++; continue; }
            try
            {
                await providers!.RememberTrackAsync(release.Execution!, song.Id);
                var artists = song.Artists.Count > 0 ? song.Artists.ToArray() : [string.IsNullOrWhiteSpace(song.Artist) ? "Unknown artist" : song.Artist];
                var result = await kept.RequestKeepAsync(release.Execution!, new(source.provider!, ProviderResourceKind.Track, source.externalId!),
                    new(song.Title, artists, song.Album, song.Duration * 1000L, song.ExplicitContentLyrics, Track: song.Track, AlbumArtist: song.AlbumArtist),
                    Guid.CreateVersion7(), cancellationToken);
                if (result.Created) queued++; else skipped++;
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException) { skipped++; }
        }
        return Accepted(new { queued, skipped });
    });

    private sealed record ReleaseAlbum(ReleaseInboxEntry? Entry, ProtocolExecutionContext? Execution, string? ProviderId,
        allstarr.Models.Domain.Album? Album, IActionResult? Failure);

    private async Task<ReleaseAlbum> ReleaseAlbumAsync(Guid owner, string entryId, CancellationToken cancellationToken)
    {
        var inbox = await releases.ReadAsync(owner, cancellationToken);
        var entry = inbox.Entries.Concat(inbox.Excluded).FirstOrDefault(item => item.Id == entryId);
        if (entry == null) return new(null, null, null, null, NotFound());
        if (protocolContexts == null || providers == null) return new(entry, null, null, null, StatusCode(503, new { error = "Track lists are unavailable." }));
        var session = (AdminAuthSession)HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey]!;
        var execution = (await protocolContexts.CreateAsync(session, HttpContext.TraceIdentifier, cancellationToken)) with { IsReadOnly = false };
        var primary = ReleaseInboxProjection.PrimarySource(entry.Sources);
        foreach (var source in entry.Sources.OrderBy(item => item == primary ? 0 : 1))
        {
            var album = await providers.GetAlbumAsync(execution, source.ProviderId, source.AlbumId);
            if (album is { Songs.Count: > 0 }) return new(entry, execution, source.ProviderId, album, null);
        }
        return new(entry, execution, null, null, null);
    }

    // A release always maps to the same mix, so adding it again restores the existing one.
    private static Guid ReleaseGuid(Guid owner, string entryId, string action) =>
        new(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{owner:N}:{entryId}:{action}")).AsSpan(0, 16));

    [HttpDelete("{entryId}")]
    public Task<IActionResult> Dismiss(string entryId, CancellationToken cancellationToken) => ExecuteAsync(async owner =>
        await releases.DismissAsync(owner, entryId, cancellationToken) ? NoContent() : NotFound());

    private async Task<IActionResult> ExecuteAsync(Func<Guid, Task<IActionResult>> action)
    {
        if (HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] is not AdminAuthSession session)
            return Unauthorized();
        if (session.AllstarrUserId is not { } owner)
            return StatusCode(403, new { error = "A linked backend user is required." });
        try { return await action(owner); }
        catch (UnauthorizedAccessException) { return StatusCode(403, new { error = "A linked listener is required." }); }
        catch (ArgumentException) { return BadRequest(new { error = "The followed artist is invalid." }); }
    }
}

public sealed record FollowReleaseArtistRequest(string ProviderId, string ArtistId, string DisplayName);
