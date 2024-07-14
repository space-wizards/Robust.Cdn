using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Robust.Cdn.Config;
using Robust.Cdn.Services;

namespace Robust.Cdn.Controllers;

[ApiController]
[Route("/version/{version}")]
public sealed class DownloadCompatibilityController(
    Database db,
    ILogger<DownloadController> logger,
    IOptionsSnapshot<CdnOptions> cdnOptions,
    DownloadRequestLogger requestLogger) : ControllerBase
{
    [HttpGet("manifest")]
    public IActionResult GetManifest(string version)
    {
        if (cdnOptions.Value.DefaultFork is not { } defaultFork)
            return NotFound();

        return GetDownloadController().GetManifest(defaultFork, version);
    }

    [HttpOptions("download")]
    public IActionResult DownloadOptions(string version)
    {
        if (cdnOptions.Value.DefaultFork is not { } defaultFork)
            return NotFound();

        return GetDownloadController().DownloadOptions(defaultFork, version);
    }

    [HttpPost("download")]
    public async Task<IActionResult> Download(string version)
    {
        if (cdnOptions.Value.DefaultFork is not { } defaultFork)
            return NotFound();

        return await GetDownloadController().Download(defaultFork, version);
    }

    private DownloadController GetDownloadController()
    {
        return new DownloadController(db, logger, cdnOptions, requestLogger)
        {
            ControllerContext = ControllerContext
        };
    }
}
