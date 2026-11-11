using allstarr.Core.ClientApi;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApi;

[Route("allstarr/v1/models/karaoke")]
public sealed class KaraokeModelController(IKaraokeModelService karaokeService) : ClientApiControllerBase
{
    [HttpGet]
    [EndpointName("getKaraokeModel")]
    [ProducesResponseType<KaraokeModelMetadata>(StatusCodes.Status200OK)]
    public ActionResult<KaraokeModelMetadata> GetMetadata()
    {
        var metadata = karaokeService.GetCurrentModelMetadata();
        if (metadata == null) return NotFound();
        return Ok(metadata);
    }

    [HttpGet("{version}/file")]
    [EndpointName("getKaraokeModelFile")]
    [ProducesResponseType(typeof(byte[]), StatusCodes.Status200OK, "application/octet-stream")]
    [ProducesResponseType(StatusCodes.Status206PartialContent)]
    public IActionResult GetModelFile(string version) => ServeModelFile(version);

    [HttpHead("{version}/file")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public IActionResult HeadModelFile(string version) => ServeModelFile(version);

    private IActionResult ServeModelFile(string version)
    {
        if (!KaraokeModelService.IsSafePathSegment(version)) return NotFound();
        var model = karaokeService.GetModel(version);
        if (model == null) return NotFound();

        Response.Headers.CacheControl = "private, max-age=31536000, immutable";

        return new PhysicalFileResult(model.PhysicalPath, "application/octet-stream")
        {
            FileDownloadName = model.FileName,
            EnableRangeProcessing = true,
            EntityTag = new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{model.Sha256}\"")
        };
    }
}
