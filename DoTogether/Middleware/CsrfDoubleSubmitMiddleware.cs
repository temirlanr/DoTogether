using System.Security.Cryptography;

namespace DoTogether.Middleware;

/// <summary>
/// Double-submit cookie CSRF protection.
/// On every response we ensure an XSRF-TOKEN cookie exists (readable by JS).
/// On state-changing requests authenticated with cookies, we require an
/// X-CSRF-Token header that matches the cookie value.
///
/// Pure-Bearer requests (no cookies present) skip the check — the bearer
/// token itself is not auto-attached by the browser and so is not CSRF-able.
/// </summary>
public sealed class CsrfDoubleSubmitMiddleware(RequestDelegate next, IWebHostEnvironment env)
{
    public const string CookieName = "XSRF-TOKEN";
    public const string HeaderName = "X-CSRF-Token";

    private static readonly HashSet<string> SafeMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "HEAD", "OPTIONS", "TRACE"
    };

    public async Task InvokeAsync(HttpContext context)
    {
        var method = context.Request.Method;
        var hasAuthCookie = context.Request.Cookies.ContainsKey(AuthCookies.RefreshCookieName);
        var isRefreshEndpoint = string.Equals(
            context.Request.Path.Value,
            "/api/Auth/refresh",
            StringComparison.OrdinalIgnoreCase);

        if (!SafeMethods.Contains(method) && hasAuthCookie && !isRefreshEndpoint)
        {
            // Skip preflight + the refresh endpoint itself; the refresh endpoint
            // proves possession via the HttpOnly refresh cookie + bearer rotation.
            var cookieToken = context.Request.Cookies[CookieName];
            var headerToken = context.Request.Headers[HeaderName].ToString();

            if (string.IsNullOrEmpty(cookieToken)
                || string.IsNullOrEmpty(headerToken)
                || !CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(cookieToken),
                    System.Text.Encoding.UTF8.GetBytes(headerToken)))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "application/problem+json";
                await context.Response.WriteAsync(
                    "{\"status\":403,\"title\":\"Forbidden\",\"code\":\"csrf_token_mismatch\",\"detail\":\"CSRF token missing or invalid.\"}");
                return;
            }
        }

        EnsureCookie(context);
        await next(context);
    }

    private void EnsureCookie(HttpContext context)
    {
        if (context.Request.Cookies.ContainsKey(CookieName)) return;

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        context.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = false, // must be readable by JS for double-submit
            Secure = !env.IsDevelopment(),
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = TimeSpan.FromDays(30)
        });
    }
}

public static class AuthCookies
{
    public const string RefreshCookieName = "dt_refresh";
}
