using allstarr.Core.Protocols;

namespace allstarr.Core.ClientApiV2;

/// <summary>
/// Reads a listener's recaps the way their app's first open does, so the song ids and refs behind each recap's top
/// songs are already known when the app asks. It runs from the warm-up, with the server's own media-server credentials
/// in place of a listener's request.
/// </summary>
public sealed class V2StatsWarmUp(V2MeEndpoints me, IBackendLibraryPermissionSource permissions, IServiceProvider services)
{
    private static readonly string[] Periods = ["30d", "7d", "year", "all"];

    public async Task WarmAsync(ProtocolExecutionContext execution, string? timeZoneId, CancellationToken token)
    {
        using var authentication = await permissions.CreateRequestAsync(execution, token);
        if (authentication == null) return;
        var http = new DefaultHttpContext { RequestServices = services };
        foreach (var header in authentication.Headers) http.Request.Headers[header.Key] = header.Value.ToArray();
        foreach (var period in Periods)
        {
            token.ThrowIfCancellationRequested();
            http.Request.QueryString = new("?period=" + period + (timeZoneId == null ? "" : "&timeZoneId=" + Uri.EscapeDataString(timeZoneId)));
            await me.ReadAsync(http, execution, "stats");
        }
    }
}
