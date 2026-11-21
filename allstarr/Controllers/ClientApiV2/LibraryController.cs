using allstarr.Core.ClientApiV2;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApiV2;

[Route("allstarr/v2/library")]
public sealed class LibraryController(V2LibrarySizeReader size) : ClientApiV2ControllerBase
{
    [HttpGet("size")]
    [EndpointName("getV2LibrarySize")]
    [EndpointDescription("Reads how much music the listener's own music libraries hold: the number of songs, their total length and the size of the files as they are. " +
        "The size at a converted quality is the length times that quality's bitrate. The first read takes a few seconds; the answer is kept for half an hour. Also available in batch.")]
    [ProducesResponseType<V2LibrarySize>(200)]
    [ProducesResponseType(304)]
    public async Task<IActionResult> Size() => Respond(await size.ReadAsync(HttpContext, Execution));
}
