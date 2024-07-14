using Microsoft.AspNetCore.Mvc;
using Quartz;
using Robust.Cdn.Helpers;
using Robust.Cdn.Jobs;

namespace Robust.Cdn.Controllers;

[ApiController]
[Route("/fork/{fork}")]
public sealed class UpdateController(
    ForkAuthHelper authHelper,
    ISchedulerFactory schedulerFactory) : ControllerBase
{
    [HttpPost("control/update")]
    public async Task<IActionResult> PostControlUpdate(string fork)
    {
        if (!authHelper.IsAuthValid(fork, out _, out var failureResult))
            return failureResult;

        var scheduler = await schedulerFactory.GetScheduler();
        await scheduler.TriggerJob(IngestNewCdnContentJob.Key, IngestNewCdnContentJob.Data(fork));

        return Accepted();
    }
}
