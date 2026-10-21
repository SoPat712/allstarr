using allstarr.Controllers;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class DownloadsQualityCheckTests
{
    [Fact]
    public async Task RecheckRequiresAdministratorBeforeLookingUpFiles()
    {
        var controller = new DownloadsController(NullLogger<DownloadsController>.Instance,
            new ConfigurationBuilder().Build())
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        var result = Assert.IsType<ObjectResult>(await controller.RecheckQuality(Guid.NewGuid()));
        Assert.Equal(403, result.StatusCode);
    }
}
