using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Quartz;
using Robust.Cdn.Config;
using Robust.Cdn.Jobs;

namespace Robust.Cdn.Controllers;

[ApiController]
[Route("/fork/{fork}")]
public sealed class UpdateController(
    IOptions<ManifestOptions> options,
    ISchedulerFactory schedulerFactory) : ControllerBase
{
    private readonly ManifestOptions _options = options.Value;

    [HttpPost("control/update")]
    public async Task<IActionResult> PostControlUpdate(string fork)
    {
        if (!_options.Forks.TryGetValue(fork, out var forkConfig))
            return NotFound();

        var authHeader = Request.Headers.Authorization;

        if (authHeader.Count == 0)
            return Unauthorized();

        var auth = authHeader[0];

        // Idk does using Bearer: make sense here?
        if (auth == null || !auth.StartsWith("Bearer "))
            return Unauthorized("Need Bearer: auth type");

        var token = auth["Bearer ".Length..];

        var matches = StringsEqual(token, forkConfig.UpdateToken);
        if (!matches)
            return Unauthorized("Incorrect token");

        var scheduler = await schedulerFactory.GetScheduler();
        await scheduler.TriggerJob(IngestNewCdnContentJob.Key, IngestNewCdnContentJob.Data(fork));

        return Accepted();
    }

    private static bool StringsEqual(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        return CryptographicOperations.FixedTimeEquals(
            MemoryMarshal.AsBytes(a),
            MemoryMarshal.AsBytes(b));
    }
}
