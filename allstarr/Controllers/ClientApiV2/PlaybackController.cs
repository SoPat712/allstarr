using allstarr.Core.ClientApiV2;
using allstarr.Filters;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApiV2;

[Route("allstarr/v2/playback")]
public sealed class PlaybackController(V2PlaybackReader playback, V2ServingStreams serving) : ClientApiV2ControllerBase
{
    [HttpGet("{playbackId}")]
    [EndpointName("getPlaybackStatus")]
    [EndpointDescription("Reads a playback created by resolve: its actual delivered format and live serving facts, both null until the stream opens and after it closes. " +
        "Never opens audio, issues a stream link or prepares a source. Another listener's playback returns 404. Retained in memory for up to twelve hours. Can also be read inside a batch.")]
    [ProducesResponseType<V2PlaybackStatus>(200)]
    [ProducesResponseType(304)]
    public IActionResult Get(string playbackId) => Respond(serving.Status(Execution, playbackId));

    [HttpPost("resolve")]
    [EndpointName("resolvePlayback")]
    [EndpointDescription("Selects a playback format and returns a signed stream URL valid for twelve hours. " +
        "cacheKey hashes the track, tier, delivered format and source identity, never the listener or signed token. " +
        "Source identity uses the library item's version stamp when available, a stored file's path, size and modification time, " +
        "or the provider track and source format. Same inputs produce the same key. " +
        "Only stored files and original/pass-through provider bytes can report seek=bytes and contentLength. " +
        "Live transcodes report seek=none and contentLength=null; Range requests return 200 with the full stream. " +
        "Clients may resume only streams reporting seek=bytes. Prefetch may move the source into cache without invalidating the URL.")]
    [ProducesResponseType<V2PlaybackResponse>(200)]
    [RequestSizeLimit(8192)]
    public async Task<IActionResult> Resolve([FromBody] V2PlaybackRequest request) =>
        Respond(await playback.ResolveAsync(HttpContext, Execution, request));
}

[ApiController]
[ApiExplorerSettings(GroupName = V2Contract.GroupName)]
[ServiceFilter(typeof(MusicPortFilter))]
[Route("allstarr/v2/stream")]
public sealed class StreamController(V2PlaybackReader playback) : ControllerBase
{
    [HttpGet("{token}")]
    [EndpointName("streamPlayback")]
    [EndpointDescription("Reads the track, delivered format and tier authorized by the signed URL without an authorization header. " +
        "The source may move between a provider and cache after prefetch. Stored files and pass-through provider streams support " +
        "byte ranges when advertised by resolve; live transcodes ignore Range and return 200 with the full stream. " +
        "Bad, expired and foreign tokens return 404.")]
    [ProducesResponseType(200)]
    [ProducesResponseType(206)]
    [ProducesResponseType(404)]
    public Task<IActionResult> Get(string token) => playback.OpenAsync(HttpContext, token);

    [HttpHead("{token}")]
    [EndpointName("headPlayback")]
    [ProducesResponseType(200)]
    [ProducesResponseType(404)]
    public Task<IActionResult> Head(string token) => playback.OpenAsync(HttpContext, token);
}
