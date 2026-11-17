using System.Net;
using Zayra.Api.Infrastructure.Common;

namespace Zayra.Api.Infrastructure.Payroll.SaudiBankExports.Api;

/// <summary>
/// The only supported way to build the HTTP handler for <see cref="AnbPayrollApiClient"/>.
/// <list type="bullet">
/// <item>Redirects are never followed. A 307/308 on the payroll POST would otherwise re-send every IBAN, ID
/// and salary to the Location host (a second POST), and on the token POST would re-send the client secret.
/// The client also refuses any handler chain whose Sockets/HttpClient handler follows redirects.</item>
/// <item>Connect-time address checks from <see cref="SsrfGuard"/>: no loopback, private, link-local or
/// metadata address is ever dialled, whatever DNS answers.</item>
/// <item>No cookies, no proxy, no automatic decompression (no gzip bomb), small response headers.</item>
/// </list>
/// The integration wave registers this as the named client's primary handler. It must not add a retry
/// handler: the no-retry rule for the payroll POST depends on it.
/// </summary>
public static class AnbHttpHandlerFactory
{
    public static SocketsHttpHandler CreateHandler()
    {
        var handler = SsrfGuard.CreateGuardedClientHandler();
        handler.AllowAutoRedirect = false;
        handler.UseCookies = false;
        handler.UseProxy = false;
        handler.AutomaticDecompression = DecompressionMethods.None;
        handler.MaxResponseHeadersLength = 64; // KB
        handler.PooledConnectionLifetime = TimeSpan.FromMinutes(5);
        handler.ConnectTimeout = TimeSpan.FromSeconds(15);
        return handler;
    }

    /// <summary>True when any handler in the chain would follow a redirect. Unknown handler types are
    /// trusted not to (a custom handler must not follow redirects either; documented, not detectable).</summary>
    public static bool FollowsRedirects(HttpMessageHandler? handler)
    {
        for (var depth = 0; handler is not null && depth < 32; depth++)
        {
            switch (handler)
            {
                case SocketsHttpHandler sockets:
                    return sockets.AllowAutoRedirect;
                case HttpClientHandler client:
                    return client.AllowAutoRedirect;
                case DelegatingHandler delegating:
                    handler = delegating.InnerHandler;
                    break;
                default:
                    return false;
            }
        }
        return false;
    }
}
