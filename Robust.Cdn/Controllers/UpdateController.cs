using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Robust.Cdn.Services;

namespace Robust.Cdn.Controllers;

[ApiController]
public sealed class UpdateController : ControllerBase
{
    private readonly DataLoader _loader;
    private readonly CdnOptions _options;

    public UpdateController(IOptionsSnapshot<CdnOptions> options, DataLoader loader)
    {
        _loader = loader;
        _options = options.Value;
    }

    [HttpPost("control/update")]
    public async Task<IActionResult> PostControlUpdate()
    {
        var authHeader = Request.Headers.Authorization;

        if (authHeader.Count == 0)
            return Unauthorized();

        var auth = authHeader[0];

        // Idk does using Bearer: make sense here?
        if (!auth.StartsWith("Bearer "))
            return Unauthorized("Need Bearer: auth type");

        var token = auth["Bearer ".Length..];

        var matches = StringsEqual(token, _options.UpdateToken);
        if (!matches)
            return Unauthorized("Incorrect token");

        await _loader.QueueUpdateVersions();
        return Accepted();
    }

    private static bool StringsEqual(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        return CryptographicOperations.FixedTimeEquals(
            MemoryMarshal.AsBytes(a),
            MemoryMarshal.AsBytes(b));
    }
}
