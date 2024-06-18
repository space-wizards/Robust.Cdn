using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Robust.Cdn.Controllers;

[ApiController]
[Route("/version/{version}")]
public sealed class DownloadCompatibilityController(
    DownloadController downloadController,
    IOptions<CdnOptions> cdnOptions) : ControllerBase
{
    [HttpGet("manifest")]
    public IActionResult GetManifest(string version)
    {
        if (cdnOptions.Value.DefaultFork is not { } defaultFork)
            return NotFound();

        return downloadController.GetManifest(defaultFork, version);
    }

    [HttpOptions("download")]
    public IActionResult DownloadOptions(string version)
    {
        if (cdnOptions.Value.DefaultFork is not { } defaultFork)
            return NotFound();

        return downloadController.DownloadOptions(defaultFork, version);
    }

    [HttpPost("download")]
    public async Task<IActionResult> Download(string version)
    {
        if (cdnOptions.Value.DefaultFork is not { } defaultFork)
            return NotFound();

        return await downloadController.Download(defaultFork, version);
    }
}
