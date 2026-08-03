using System.Text.Json.Serialization;
using allstarr.Core.Intelligence;
using allstarr.Core.Playback;
using allstarr.Core.Protocols;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

[ApiController]
[Route("apis/listenbrainz/1")]
public sealed class ListenBrainzIntakeController(
    ListeningIntakeTokenService tokens,
    IPlaybackSignalPipeline playback,
    TimeProvider timeProvider) : ControllerBase
{
    private const int MaxRows = 100;
    private static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(5);

    [HttpGet("validate-token")]
    public async Task<IActionResult> ValidateToken(CancellationToken cancellationToken)
    {
        var grant = await tokens.AuthorizeAsync(AccessToken(), cancellationToken);
        return grant == null
            ? Unauthorized(new { code = 401, valid = false })
            : Ok(new { code = 200, valid = true, user_name = grant.Principal.DisplayName });
    }

    [HttpPost("submit-listens")]
    [RequestSizeLimit(1_048_576)]
    public async Task<IActionResult> SubmitListens(
        [FromBody] ListenBrainzSubmitRequest request,
        CancellationToken cancellationToken)
    {
        var grant = await tokens.AuthorizeAsync(AccessToken(), cancellationToken);
        if (grant == null) return Unauthorized(new { code = 401, error = "invalid_token" });
        var now = timeProvider.GetUtcNow();
        if (!TryNormalize(request, now, out var listens, out var error))
            return BadRequest(new { code = 400, error });

        var correlation = HttpContext.TraceIdentifier;
        if (correlation.Length > 80) correlation = correlation[..80];
        for (var index = 0; index < listens.Count; index++)
        {
            var listen = listens[index];
            var principal = grant.Principal;
            var context = new ProtocolExecutionContext(
                grant.Protocol,
                grant.Scope.BackendInstanceId,
                principal.BackendPrincipalId,
                principal,
                $"{correlation}-{index}",
                now.AddSeconds(30),
                cancellationToken,
                new("listenbrainz-api", listen.MediaPlayer, listen.MediaPlayer),
                grant.Scope.LibraryScopeId);
            var identity = PlaybackSignalPipeline.Hash($"{listen.RecordingMusicBrainzId}|{listen.Artist.ToUpperInvariant()}|{listen.Title.ToUpperInvariant()}|{listen.Album?.ToUpperInvariant()}");
            var itemId = $"listenbrainz:{identity}";
            var occurrence = PlaybackSignalPipeline.Hash($"{identity}|{listen.ObservedAt.ToUnixTimeSeconds()}");
            var track = new PlaybackTrackSnapshot(
                null, null, itemId, listen.Title, listen.Artist, listen.Album,
                listen.DurationMilliseconds, RecordingMusicBrainzId: listen.RecordingMusicBrainzId,
                TrackNumber: listen.TrackNumber, Isrc: listen.Isrc);
            await playback.RecordAsync(new(
                context,
                listen.PlayingNow ? PlaybackTransition.Start : PlaybackTransition.Submission,
                itemId,
                listen.MediaPlayer,
                $"listenbrainz:{occurrence}",
                null,
                listen.ObservedAt,
                track,
                grant.RelayExternally,
                "listenbrainz-api"), cancellationToken);
        }

        return Ok(new { status = "ok" });
    }

    private string? AccessToken()
    {
        var value = Request.Headers.Authorization.ToString();
        return value.StartsWith("Token ", StringComparison.OrdinalIgnoreCase)
            ? value[6..].Trim()
            : null;
    }

    internal static bool TryNormalize(
        ListenBrainzSubmitRequest request,
        DateTimeOffset now,
        out List<NormalizedListen> listens,
        out string error)
    {
        listens = [];
        error = "invalid_payload";
        var type = request.ListenType;
        var rows = request.Payload;
        if (rows == null || rows.Count == 0 || rows.Count > MaxRows ||
            type is not ("single" or "import" or "playing_now") ||
            type != "import" && rows.Count != 1)
            return false;

        foreach (var row in rows)
        {
            if (row?.TrackMetadata == null ||
                !TryText(row.TrackMetadata.TrackName, 500, true, out var title) ||
                !TryText(row.TrackMetadata.ArtistName, 500, true, out var artist) ||
                !TryText(row.TrackMetadata.ReleaseName, 500, false, out var album))
                return false;
            var extra = row.TrackMetadata.AdditionalInfo;
            if (extra?.DurationMilliseconds is <= 0 or > 86_400_000 ||
                extra?.TrackNumber is <= 0 or > 10_000 ||
                !TryText(extra?.MediaPlayer, 200, false, out var mediaPlayer) ||
                !TryText(extra?.Isrc, 20, false, out var isrc) ||
                !TryMusicBrainzId(extra?.RecordingMusicBrainzId, out var recordingId))
                return false;

            DateTimeOffset observedAt;
            if (type == "playing_now")
            {
                observedAt = now;
            }
            else
            {
                if (row.ListenedAt is not > 0) return false;
                try { observedAt = DateTimeOffset.FromUnixTimeSeconds(row.ListenedAt.Value); }
                catch (ArgumentOutOfRangeException) { return false; }
                if (observedAt > now + MaxFutureSkew) return false;
            }

            listens.Add(new(
                title!, artist!, album, extra?.DurationMilliseconds, recordingId,
                extra?.TrackNumber, isrc, mediaPlayer, observedAt, type == "playing_now"));
        }

        error = "";
        return true;
    }

    private static bool TryText(string? value, int maxLength, bool required, out string? result)
    {
        result = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return result == null
            ? !required
            : result.Length <= maxLength && !result.Any(char.IsControl);
    }

    private static bool TryMusicBrainzId(string? value, out string? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!Guid.TryParseExact(value.Trim(), "D", out var id) || id == Guid.Empty) return false;
        result = id.ToString("D");
        return true;
    }
}

public sealed class ListenBrainzSubmitRequest
{
    [JsonPropertyName("listen_type")]
    public string ListenType { get; set; } = "";

    [JsonPropertyName("payload")]
    public List<ListenBrainzListen?>? Payload { get; set; }
}

public sealed class ListenBrainzListen
{
    [JsonPropertyName("listened_at")]
    public long? ListenedAt { get; set; }

    [JsonPropertyName("track_metadata")]
    public ListenBrainzTrackMetadata? TrackMetadata { get; set; }
}

public sealed class ListenBrainzTrackMetadata
{
    [JsonPropertyName("artist_name")]
    public string? ArtistName { get; set; }

    [JsonPropertyName("track_name")]
    public string? TrackName { get; set; }

    [JsonPropertyName("release_name")]
    public string? ReleaseName { get; set; }

    [JsonPropertyName("additional_info")]
    public ListenBrainzAdditionalInfo? AdditionalInfo { get; set; }
}

public sealed class ListenBrainzAdditionalInfo
{
    [JsonPropertyName("duration_ms")]
    public long? DurationMilliseconds { get; set; }

    [JsonPropertyName("recording_mbid")]
    public string? RecordingMusicBrainzId { get; set; }

    [JsonPropertyName("tracknumber")]
    public int? TrackNumber { get; set; }

    [JsonPropertyName("isrc")]
    public string? Isrc { get; set; }

    [JsonPropertyName("media_player")]
    public string? MediaPlayer { get; set; }
}

public sealed record NormalizedListen(
    string Title,
    string Artist,
    string? Album,
    long? DurationMilliseconds,
    string? RecordingMusicBrainzId,
    int? TrackNumber,
    string? Isrc,
    string? MediaPlayer,
    DateTimeOffset ObservedAt,
    bool PlayingNow);
