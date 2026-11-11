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

    /// <summary>Reads a comma-separated list of ids; false when it is empty, too long or has an empty entry.</summary>
    protected static bool TryIds(string? value, out string[] ids)
    {
        ids = value?.Split(',', StringSplitOptions.TrimEntries) ?? [];
        return ids.Length is > 0 and <= V2Contract.MaximumIdsPerRequest && ids.All(id => id.Length > 0);
    }

    /// <summary>Reads <c>include=</c>; an unknown name is an error so a typo never looks like missing data.</summary>
    protected static bool TryInclude(string? value, IReadOnlySet<string> allowed, out HashSet<string> include)
    {
        include = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(value)) return true;
        foreach (var name in value.Split(',', StringSplitOptions.TrimEntries))
            if (!allowed.Contains(name) || !include.Add(name)) return false;
        return true;
    }
}
