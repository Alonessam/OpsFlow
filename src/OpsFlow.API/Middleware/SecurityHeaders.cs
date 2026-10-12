namespace OpsFlow.API.Middleware;

public static class SecurityHeaders
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; " +
        "connect-src 'self' ws: wss:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";

    /// <summary>
    /// Applied by the normal pipeline AND again by the exception handler, because ASP.NET Core's
    /// exception middleware clears the response (headers included) before it runs the handler.
    /// </summary>
    public static void Apply(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Content-Security-Policy"] = ContentSecurityPolicy;

        // Authenticated API data must never sit in a browser or proxy cache.
        if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/hubs"))
        {
            headers["Cache-Control"] = "no-store";
        }
    }
}
