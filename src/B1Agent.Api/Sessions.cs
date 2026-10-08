using System.Security.Cryptography;
using B1Agent.Core.Connections;

namespace B1Agent.Api;

/// <summary>
/// An anonymous browser session: a random id in an HttpOnly cookie. It scopes customer Service Layer connections
/// and quotation proposals to the browser that created them. It is not authentication.
/// </summary>
public sealed class CookieSessionKey(IHttpContextAccessor accessor) : ISapSessionKey
{
    public const string CookieName = "b1agent.sid";
    private const string ItemKey = "b1agent.sid";

    public string? Current => accessor.HttpContext?.Items[ItemKey] as string;

    /// <summary>Reads the session cookie, or issues one, before the endpoints run.</summary>
    public static async Task Middleware(HttpContext context, Func<Task> next)
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            var id = context.Request.Cookies[CookieName];
            if (id is not { Length: 43 })
            {
                id = Base64Url(RandomNumberGenerator.GetBytes(32));
                context.Response.Cookies.Append(CookieName, id, new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.Strict,
                    IsEssential = true,
                    Path = "/api"
                });
            }
            context.Items[ItemKey] = id;
        }
        await next();
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public static class ClientAddress
{
    /// <summary>The visitor's IP. Behind Cloudflare the socket peer is the tunnel, so prefer CF-Connecting-IP.</summary>
    public static string Of(HttpContext context) =>
        context.Request.Headers["CF-Connecting-IP"].FirstOrDefault()
        ?? context.Connection.RemoteIpAddress?.ToString()
        ?? "unknown";
}
