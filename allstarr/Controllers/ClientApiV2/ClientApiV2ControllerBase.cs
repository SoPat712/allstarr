using allstarr.Core.ClientApi;
using allstarr.Core.ClientApiV2;
using allstarr.Core.Protocols;
using allstarr.Filters;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApiV2;

[ApiController]
[ApiExplorerSettings(GroupName = V2Contract.GroupName)]
[Produces("application/json")]
[ProducesResponseType<ClientApiError>(400)]
[ProducesResponseType<ClientApiError>(401)]
[ProducesResponseType<ClientApiError>(403)]
[ProducesResponseType<ClientApiError>(404)]
[ProducesResponseType<ClientApiError>(503)]
[ServiceFilter(typeof(MusicPortFilter))]
[ServiceFilter(typeof(ClientApiResultFilter))]
[ServiceFilter(typeof(JellyfinAuthFilter), Order = int.MinValue)]
[ServiceFilter(typeof(ProtocolExecutionContextFilter), Order = int.MinValue + 1)]
[ServiceFilter(typeof(ClientApiListenerFilter), Order = int.MinValue + 2)]
public abstract class ClientApiV2ControllerBase : ControllerBase
{
    protected ProtocolExecutionContext Execution => HttpContext.RequireProtocolExecutionContext();

    /// <summary>Writes a shared route's answer; errors become the API's <c>{code,error}</c> shape.</summary>
    protected IActionResult Respond(V2Result result)
    {
        Response.Headers.CacheControl = "no-store";
        if (HttpMethods.IsGet(Request.Method))
        {
            result = result.Conditional(Request.Headers.IfNoneMatch);
            if (result.ETag != null) Response.Headers.ETag = result.ETag;
        }
        if (result.Body is ClientApiError error) return new JsonResult(error) { StatusCode = result.Status };
        return result.Status == 200 && result.Body != null ? Ok(result.Body) : StatusCode(result.Status);
    }
}
