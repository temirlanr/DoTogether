namespace DoTogether.Middleware;

/// <summary>
/// Adds baseline OWASP-recommended security headers to every response.
/// API-only service: strict CSP (no scripts, no embedding), no Referer leak.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next, IWebHostEnvironment env)
{
    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;

        context.Response.OnStarting(() =>
        {
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";
            headers["Cross-Origin-Resource-Policy"] = "same-site";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";

            if (context.Request.Path.StartsWithSegments("/api"))
            {
                headers["Cache-Control"] = "no-store, max-age=0";
                headers["Pragma"] = "no-cache";
                headers["Expires"] = "0";
            }

            // CSP locked down — this service serves JSON only. Swagger UI in dev relaxes via a separate path-based check.
            if (env.IsDevelopment() && context.Request.Path.StartsWithSegments("/swagger"))
            {
                headers["Content-Security-Policy"] =
                    "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; connect-src 'self'";
            }
            else
            {
                headers["Content-Security-Policy"] =
                    "default-src 'none'; frame-ancestors 'none'; base-uri 'none'";
            }

            if (!env.IsDevelopment())
            {
                headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
            }

            return Task.CompletedTask;
        });

        return next(context);
    }
}
