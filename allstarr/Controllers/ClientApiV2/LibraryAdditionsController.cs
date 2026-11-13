using System.ComponentModel;
using allstarr.Core.ClientApi;
using allstarr.Core.ClientApiV2;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApiV2;

[Route("allstarr/v2/library/additions")]
[RequestSizeLimit(8192)]
public sealed class LibraryAdditionsController(V2LibraryEndpoints library) : ClientApiV2ControllerBase
{
    [HttpGet]
    [EndpointName("getV2LibraryAdditions")]
    [EndpointDescription("Lists only this listener's library additions, newest first. Also available in batch.")]
    [ProducesResponseType<V2LibraryAddition[]>(200)]
    [ProducesResponseType(304)]
    public async Task<IActionResult> List() => Respond(await library.AdditionsAsync(Execution));

    [HttpPost]
    [EndpointName("addV2LibraryItem")]
    [EndpointDescription("Adds or requests a song or album using the listener's promotion profile and approval rules. A replayed request key returns the saved addition.")]
    [ProducesResponseType<V2LibraryAddition>(202)]
    [ProducesResponseType<V2LibraryAddition>(200)]
    [ProducesResponseType<ClientApiError>(409)]
    public async Task<IActionResult> Add([FromBody] V2AddLibraryItem request,
        [FromHeader(Name = "Idempotency-Key"), Description("Optional retry key, at most 300 characters; reusing it with changed input returns 409.")] string? key)
    {
        var retryKey = key ?? (Request.Headers.TryGetValue("Idempotency-Key", out var supplied) ? supplied.ToString() : null);
        return Respond(await library.AddAsync(Execution, request, retryKey));
    }

    [HttpDelete("{id}")]
    [EndpointName("cancelV2LibraryAddition")]
    [EndpointDescription("Cancels the listener's pending request or running addition. Files already added stay in the library. Another listener's id returns 404.")]
    [ProducesResponseType<V2LibraryAddition>(200)]
    [ProducesResponseType<ClientApiError>(409)]
    public async Task<IActionResult> Cancel(string id) => Respond(await library.CancelAsync(Execution, id));
}
