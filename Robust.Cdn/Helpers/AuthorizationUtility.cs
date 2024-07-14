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

    public static bool CheckBasicAuth(
        HttpContext httpContext,
        string realm,
        Func<string, string?> getPassword,
        [NotNullWhen(true)] out string? user,
        [NotNullWhen(false)] out IActionResult? failure)
    {
        user = null;

        if (!httpContext.Request.Headers.TryGetValue("Authorization", out var authValues))
        {
            SetWwwAuthenticate(httpContext, realm);
            failure = new UnauthorizedResult();
            return false;
        }

        var authValue = authValues[0]!;
        if (!TryParseBasicAuthentication(
                authValue,
                out failure,
                out user,
                out var password))
        {
            SetWwwAuthenticate(httpContext, realm);
            return false;
        }

        var expectedPassword = getPassword(user);
        if (expectedPassword == null)
        {
            SetWwwAuthenticate(httpContext, realm);
            failure = new UnauthorizedResult();
            return false;
        }

        if (!BasicAuthMatches(password, expectedPassword))
        {
            SetWwwAuthenticate(httpContext, realm);
            failure = new UnauthorizedResult();
            return false;
        }

        return true;
    }

    private static void SetWwwAuthenticate(HttpContext context, string realm)
    {
        context.Response.Headers.WWWAuthenticate = $"Basic realm={realm}";
    }
}
