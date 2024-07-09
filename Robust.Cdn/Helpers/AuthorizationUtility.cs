using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace Robust.Cdn.Helpers;

public static class AuthorizationUtility
{
    public static bool TryParseBasicAuthentication(string authorization,
        [NotNullWhen(false)] out IActionResult? failure,
        [NotNullWhen(true)] out string? username,
        [NotNullWhen(true)] out string? password)
    {
        username = null;
        password = null;

        if (!authorization.StartsWith("Basic "))
        {
            failure = new UnauthorizedResult();
            return false;
        }

        var value = Encoding.UTF8.GetString(Convert.FromBase64String(authorization[6..]));
        var split = value.Split(':');

        if (split.Length != 2)
        {
            failure = new BadRequestResult();
            return false;
        }

        username = split[0];
        password = split[1];
        failure = null;
        return true;
    }

    public static bool BasicAuthMatches(string provided, string expected)
    {
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided),
            Encoding.UTF8.GetBytes(expected));
    }
}
