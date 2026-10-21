using allstarr.Services.Subsonic;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

public partial class SubsonicController
{
    [HttpGet, HttpPost]
    [Route("rest/ping")]
    [Route("rest/ping.view")]
    public async Task<IActionResult> Ping()
    {
        var parameters = await ExtractAllParameters();
        var format = SonicFormat(parameters);
        try
        {
            var upstream = await _proxyService.RelayRawAsync(Request.Path.Value!, parameters,
                HttpContext.RequestAborted, Request.Headers);
            return _relayProtocolAdapter.CreateResult(SubsonicLyricsDiscovery.Merge(upstream, format, ping: true),
                $"application/{format}");
        }
        catch (HttpRequestException)
        {
            return _responseBuilder.CreateError(format, 0, "Error connecting to Subsonic server");
        }
    }
}
