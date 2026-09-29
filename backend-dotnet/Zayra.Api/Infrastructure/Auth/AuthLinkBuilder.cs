namespace Zayra.Api.Infrastructure.Auth;

/// <summary>
/// Canonical browser credential links. Secrets always live in the fragment so
/// they are not sent in the initial HTTP request, proxy logs, or referrers.
/// </summary>
public static class AuthLinkBuilder
{
    public static string ResetPassword(string? appUrl, string workspace, string token) =>
        Build(appUrl, "/reset-password", workspace, token);

    public static string AcceptInvitation(string? appUrl, string workspace, string token) =>
        Build(appUrl, "/accept-invitation", workspace, token);

    public static string RequireHttpsPublicAppUrl(string? appUrl)
    {
        if (string.IsNullOrWhiteSpace(appUrl)
            || !Uri.TryCreate(appUrl.Trim(), UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Host)
            || uri.IsLoopback
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.AbsolutePath.Trim('/')))
        {
            throw new InvalidOperationException(
                "APP_URL must be an absolute HTTPS public application URL with no credentials, query, or fragment.");
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }

    public static string ResolvePublicAppUrl(string? appUrl)
    {
        var configured = string.IsNullOrWhiteSpace(appUrl)
            ? "http://localhost:3000"
            : appUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.AbsolutePath.Trim('/'))
            || (uri.Scheme != Uri.UriSchemeHttps
                && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        {
            throw new InvalidOperationException(
                "APP_URL must be HTTPS (HTTP is allowed only for loopback development) and cannot contain credentials, query, or fragment.");
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }

    private static string Build(string? appUrl, string path, string workspace, string token)
    {
        // Unit/local-development callers have a deterministic browser-resolvable
        // default. Every non-Development host validates an explicit HTTPS value
        // during startup (Program.cs) before it can issue credentials.
        var baseUrl = ResolvePublicAppUrl(appUrl);
        var encodedWorkspace = Uri.EscapeDataString(AuthService.RequireWorkspace(workspace));
        var encodedToken = Uri.EscapeDataString(token);
        return $"{baseUrl}{path}?workspace={encodedWorkspace}#token={encodedToken}";
    }
}
