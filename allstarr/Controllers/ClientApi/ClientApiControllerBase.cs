using allstarr.Core.ClientApi;
using allstarr.Core.Protocols;
using allstarr.Filters;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApi;

[ApiController]
[ApiExplorerSettings(GroupName = ClientApiContract.GroupName)]
[Produces("application/json")]
[ProducesResponseType<ClientApiError>(400)]
[ProducesResponseType<ClientApiError>(401)]
[ProducesResponseType<ClientApiError>(403)]
[ProducesResponseType<ClientApiError>(404)]
[ProducesResponseType<ClientApiError>(409)]
[ProducesResponseType<ClientApiError>(503)]
[ServiceFilter(typeof(MusicPortFilter))]
[ServiceFilter(typeof(ClientApiResultFilter))]
[ServiceFilter(typeof(JellyfinAuthFilter), Order = int.MinValue)]
[ServiceFilter(typeof(ProtocolExecutionContextFilter), Order = int.MinValue + 1)]
[ServiceFilter(typeof(ClientApiListenerFilter), Order = int.MinValue + 2)]
public abstract class ClientApiControllerBase : ControllerBase
{
    protected ProtocolExecutionContext Execution => HttpContext.RequireProtocolExecutionContext();
}
