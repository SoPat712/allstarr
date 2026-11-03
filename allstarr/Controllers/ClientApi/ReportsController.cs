using allstarr.Core.ClientApi;
using allstarr.Core.Operations;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApi;

[Route("allstarr/v1/reports")]
[RequestSizeLimit(16384)]
public sealed class ReportsController(ProblemReportService reports) : ClientApiControllerBase
{
    [HttpPost]
    [EndpointName("createReport")]
    [ProducesResponseType<ClientReportReceipt>(201)]
    public async Task<ActionResult<ClientReportReceipt>> Create([FromBody] CreateClientReportRequest request)
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            var result = await reports.CreateListenerAsync(Execution, request, Execution.CancellationToken);
            return StatusCode(201, new ClientReportReceipt(result.Id));
        }
        catch (ProblemReportException error) { return StatusCode(error.StatusCode); }
        catch (UnauthorizedAccessException) { return NotFound(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException) { return BadRequest(); }
        catch (HttpRequestException) { return StatusCode(503); }
    }
}
