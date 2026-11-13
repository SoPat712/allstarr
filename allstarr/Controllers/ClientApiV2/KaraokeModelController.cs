using System.ComponentModel;
using allstarr.Core.ClientApi;
using allstarr.Core.ClientApiV2;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApiV2;

[Route("allstarr/v2/models/karaoke")]
public sealed class KaraokeModelController(V2LibraryEndpoints library, IKaraokeModelService karaoke) : ClientApiV2ControllerBase
{
    [HttpGet]
    [EndpointName("getV2KaraokeModel")]
    [EndpointDescription("Reads the current validated karaoke model's version, checksum, size, compute target and download URL. Also available in batch.")]
    [ProducesResponseType<V2KaraokeModel>(200)]
    [ProducesResponseType(304)]
    public IActionResult Metadata() => Respond(library.Karaoke());

    [HttpGet("{version}/file")]
    [EndpointName("getV2KaraokeModelFile")]
    [EndpointDescription("Downloads a validated model with byte ranges and an immutable checksum ETag. Unknown versions return 404.")]
    [ProducesResponseType(typeof(byte[]), 200, "application/octet-stream")]
    [ProducesResponseType(206)]
    [ProducesResponseType(304)]
    [ProducesResponseType(416)]
    public IActionResult Download([Description("Version returned by the karaoke model resource.")] string version)
    {
        if (version.Length > 128 || version.Any(char.IsControl) || !KaraokeModelService.IsSafePathSegment(version)) return Respond(new(400, null));
        var model = karaoke.GetModel(version);
        if (model == null) return Respond(new(404, null));
        Response.Headers.CacheControl = "private, max-age=31536000, immutable";
        return new PhysicalFileResult(model.PhysicalPath, "application/octet-stream")
        {
            FileDownloadName = model.FileName,
            EnableRangeProcessing = true,
            EntityTag = new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{model.Sha256}\"")
        };
    }
}
