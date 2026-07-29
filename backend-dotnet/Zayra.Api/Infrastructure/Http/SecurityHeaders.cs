using Microsoft.AspNetCore.Http;

namespace Zayra.Api.Infrastructure.Http;

/// <summary>
/// Central response-header policy: security headers (incl. CSP/HSTS/Permissions-Policy) plus
/// path-aware Cache-Control. Extracted from the Program.cs middleware so it can be unit-tested
/// without booting the host. Applied on every response before the endpoint runs.
/// </summary>
public static class SecurityHeaders
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
        "connect-src 'self'; frame-ancestors 'none'; object-src 'none'; base-uri 'self'; form-action 'self'";

    public static void Apply(IHeaderDictionary headers, string path)
    {
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY"; // legacy backstop to CSP frame-ancestors 'none'
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        headers["X-Permitted-Cross-Domain-Policies"] = "none";
        // HSTS — safe behind Render's TLS terminator; 2y + preload. Not path-dependent.
        headers["Strict-Transport-Security"] = "max-age=63072000; includeSubDomains; preload";
        // Lock down powerful browser features the API never needs.
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";

        path ??= string.Empty;

        // Content-Security-Policy: script-src 'self' (no 'unsafe-inline') neutralises any injected
        // inline script/handler in stored offer/contract HTML; frame-ancestors 'none' blocks clickjacking.
        // style-src allows 'unsafe-inline' so the offer letter's inline <style> still renders.
        // Swagger UI (Development only) ships an inline bootstrap script, so /swagger is exempted.
        if (!path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase))
            headers["Content-Security-Policy"] = ContentSecurityPolicy;

        // Sensitive: auth, payroll, personal data — must never be cached anywhere.
        if (path.StartsWith("/api/auth", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/payroll", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/employees", StringComparison.OrdinalIgnoreCase))
        {
            headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
            headers["Pragma"] = "no-cache";
        }
        // Semi-static reference data: short private cache so browser avoids round trips.
        else if (path.StartsWith("/api/master-data", StringComparison.OrdinalIgnoreCase) ||
                 path.StartsWith("/api/features", StringComparison.OrdinalIgnoreCase) ||
                 path.StartsWith("/api/localization", StringComparison.OrdinalIgnoreCase) ||
                 path.StartsWith("/api/help-text", StringComparison.OrdinalIgnoreCase))
        {
            headers["Cache-Control"] = "private, max-age=300"; // 5 min, per-user
        }
        // Everything else: don't cache by default; controllers can override explicitly.
        else
        {
            headers["Cache-Control"] = "no-store";
        }
    }
}
