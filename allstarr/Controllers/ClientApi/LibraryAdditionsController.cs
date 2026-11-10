using allstarr.Core.ClientApi;
using allstarr.Core.Enrichment;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApi;

[Route("allstarr/v1/library/additions")]
public sealed class LibraryAdditionsController(ClientLibraryAdditions additions) : ClientApiControllerBase
{
    [HttpGet]
    [EndpointName("getLibraryAdditions")]
    [ProducesResponseType<ClientLibraryAddition[]>(200)]
    public Task<ActionResult<ClientLibraryAddition[]>> Get(CancellationToken token) => ExecuteAsync(() => additions.ListAsync(Execution, token));

    [HttpPost]
    [EndpointName("addLibraryItem")]
    [ProducesResponseType<ClientLibraryAddition>(202)]
    public Task<ActionResult<ClientLibraryAddition>> Add([FromBody] AddClientLibraryItem request, CancellationToken token) =>
        ExecuteAsync(() => additions.AddAsync(Execution, request, token), accepted: true);

    [HttpPost("{id:guid}/cancel")]
    [EndpointName("cancelLibraryAddition")]
    [ProducesResponseType<ClientLibraryAddition>(200)]
    public Task<ActionResult<ClientLibraryAddition>> Cancel(Guid id, CancellationToken token) => ExecuteAsync(() => additions.CancelAsync(Execution, id, token));

    private async Task<ActionResult<T>> ExecuteAsync<T>(Func<Task<T>> action, bool accepted = false)
    {
        Response.Headers.CacheControl = "private, no-store";
        try { var value = await action(); return accepted ? Accepted(value) : Ok(value); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return NotFound(); }
        catch (ArgumentException) { return BadRequest(); }
        catch (PromotionException error) { return StatusCode(error.Status, new ClientApiError(ClientApiError.ForStatus(error.Status).Code, error.Message)); }
    }
}
