using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Robust.Cdn.Config;

namespace Robust.Cdn.Helpers;

public sealed class ForkAuthHelper(IHttpContextAccessor accessor, IOptions<ManifestOptions> options)
{
    public bool IsAuthValid(
        string fork,
        [NotNullWhen(true)] out ManifestForkOptions? forkConfig,
        [NotNullWhen(false)] out IActionResult? failureResult)
    {
        if (!options.Value.Forks.TryGetValue(fork, out forkConfig))
        {
            failureResult = new NotFoundResult();
            return false;
        }

        var context = accessor.HttpContext ?? throw new InvalidOperationException("Unable to get HttpContext");

        var authHeader = context.Request.Headers.Authorization;
        if (authHeader.Count == 0)
        {
            failureResult = new UnauthorizedResult();
            return false;
        }

        var auth = authHeader[0];

        // Idk does using Bearer: make sense here?
        if (auth == null || !auth.StartsWith("Bearer "))
        {
            failureResult = new UnauthorizedObjectResult("Need Bearer: auth type");
            return false;
        }

        var token = auth["Bearer ".Length..];

        var matches = StringsEqual(token, forkConfig.UpdateToken);
        if (!matches)
        {
            failureResult = new UnauthorizedObjectResult("Incorrect token");
            return false;
        }

        failureResult = null;
        return true;
    }

    private static bool StringsEqual(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        return CryptographicOperations.FixedTimeEquals(
            MemoryMarshal.AsBytes(a),
            MemoryMarshal.AsBytes(b));
    }
}
