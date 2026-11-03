using System.ComponentModel.DataAnnotations;
using allstarr.Core.ClientApi;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Controllers.ClientApi;

[Route("allstarr/v1/items/{itemId}/match")]
public sealed class ItemMatchController(ItemMatchReader reader) : ClientApiControllerBase
{
    [HttpGet]
    [EndpointName("getItemMatch")]
    [ProducesResponseType<ClientItemMatch>(200)]
    public Task<ActionResult<ClientItemMatch>> Get(string itemId) =>
        ExecuteAsync(() => reader.ReadAsync(HttpContext, Execution, itemId));

    [HttpPut]
    [EndpointName("setItemMatch")]
    [ProducesResponseType<ClientItemMatch>(200)]
    public Task<ActionResult<ClientItemMatch>> Set(string itemId, [FromBody] SetClientMatchRequest request) =>
        ExecuteAsync(() => reader.SetAsync(HttpContext, Execution, itemId, request));

    [HttpPost("reject")]
    [EndpointName("rejectItemMatch")]
    [ProducesResponseType<ClientItemMatch>(200)]
    public Task<ActionResult<ClientItemMatch>> Reject(string itemId, [FromBody] RejectClientMatchRequest request) =>
        ExecuteAsync(() => reader.RejectAsync(HttpContext, Execution, itemId, request));

    [HttpDelete]
    [EndpointName("clearItemMatch")]
    [ProducesResponseType<ClientItemMatch>(200)]
    public Task<ActionResult<ClientItemMatch>> Clear(string itemId, [FromQuery, Required] string expectedRevision) =>
        ExecuteAsync(() => reader.ClearAsync(HttpContext, Execution, itemId, expectedRevision));

    private async Task<ActionResult<T>> ExecuteAsync<T>(Func<Task<T?>> action) where T : class
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            var result = await action();
            return result == null ? NotFound() : Ok(result);
        }
        catch (UnauthorizedAccessException) { return NotFound(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (DbUpdateConcurrencyException) { return Conflict(); }
        catch (Exception error) when (DbErrors.IsTransientConflict(error) || DbErrors.IsUniqueViolation(error)) { return Conflict(); }
        catch (ArgumentException) { return BadRequest(); }
        catch (HttpRequestException) { return StatusCode(503); }
    }
}
