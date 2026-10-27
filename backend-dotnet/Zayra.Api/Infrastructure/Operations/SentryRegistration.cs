using Sentry;
using Sentry.Extensibility;
using Zayra.Api.Infrastructure.Http;

namespace Zayra.Api.Infrastructure.Operations;

/// <summary>
/// Error tracking, reached only through <see cref="Observability"/> when <c>SENTRY_DSN</c> is set. Isolated in its
/// own class (with NoInlining bridges) so no Sentry type appears in <see cref="Observability"/>'s IL: with the DSN
/// unset the Sentry assemblies never load. HTTP responses are untouched; <c>ExceptionResponseMapper</c> still owns
/// them. This class and <see cref="SentryPrivacy"/> are the single scrub point for the API.
/// </summary>
internal static class SentryRegistration
{
    /// <summary>The ILogger category the global exception handler logs under (Program.cs).</summary>
    private const string GlobalHandlerCategory = "GlobalExceptionHandler";

    public static void Register(WebApplicationBuilder builder)
    {
        var configuration = builder.Configuration;
        builder.WebHost.UseSentry(options =>
        {
            options.Dsn = configuration[Observability.SentryDsnKey]!.Trim();
            options.Environment = FirstNonEmpty(configuration[Observability.SentryEnvironmentKey], builder.Environment.EnvironmentName);
            options.Release = BuildInfo.Commit;
            options.TracesSampleRate = SentryPrivacy.ParseTracesSampleRate(configuration[Observability.SentryTracesSampleRateKey]);
            options.SendDefaultPii = false;               // no IP address, user, cookies
            options.MaxRequestBodySize = RequestSize.None; // never a request body
            options.CaptureFailedRequests = false;     // outbound URLs carry ids (Qiwa) and device credentials
            options.AutoSessionTracking = false;
            options.MinimumBreadcrumbLevel = Microsoft.Extensions.Logging.LogLevel.Warning;
            options.SetBeforeSend((evt, _) => BeforeSend(evt));
            options.SetBeforeBreadcrumb((crumb, _) => BeforeBreadcrumb(crumb));
            options.SetBeforeSendTransaction((tx, _) => BeforeSendTransaction(tx));
        });
    }

    /// <summary>Tags the current request's scope with the opaque tenant GUID. Never a name.</summary>
    public static void TagTenant(string tenantId)
    {
        if (SentryPrivacy.TryOpaqueTenantId(tenantId, out var id))
            SentrySdk.ConfigureScope(scope => scope.SetTag("tenant_id", id));
    }

    /// <summary>The single scrub point. Returns null to drop the event.</summary>
    internal static SentryEvent? BeforeSend(SentryEvent evt)
    {
        // The global handler logs every exception it maps, including the 4xx business-rule ones; only 5xx is an incident.
        if (evt.Logger == GlobalHandlerCategory && evt.Exception is { } mapped && ExceptionResponseMapper.Map(mapped).StatusCode < 500)
            return null;

        ScrubRequest(evt.Request);

        evt.User.Email = null;
        evt.User.Username = null;
        evt.User.IpAddress = null;
        evt.User.Id = null;

        if (evt.Message is { } message)
        {
            message.Message = SentryPrivacy.ScrubText(message.Message);
            message.Formatted = SentryPrivacy.ScrubText(message.Formatted);
        }

        if (evt.SentryExceptions is { } exceptions)
            foreach (var exception in exceptions)
                exception.Value = SentryPrivacy.ScrubText(exception.Value);

        evt.ServerName = null; // the host name adds nothing the environment does not
        return evt;
    }

    internal static SentryTransaction? BeforeSendTransaction(SentryTransaction tx)
    {
        ScrubRequest(tx.Request);
        tx.User.Email = null;
        tx.User.Username = null;
        tx.User.IpAddress = null;
        tx.User.Id = null;
        return tx;
    }

    /// <summary>Drops a breadcrumb that carries a query string or any personal-data shape, rather than guess at a rewrite.</summary>
    internal static Breadcrumb? BeforeBreadcrumb(Breadcrumb crumb)
    {
        if (SentryPrivacy.IsDirty(crumb.Message)) return null;
        if (crumb.Data is { } data)
            foreach (var value in data.Values)
                if (SentryPrivacy.IsDirty(value)) return null;
        return crumb;
    }

    private static void ScrubRequest(SentryRequest request)
    {
        request.Data = null;
        request.Cookies = null;
        request.QueryString = null;
        request.Headers.Clear(); // Authorization, Cookie, forwarding secrets
        request.Env.Clear();
        request.Url = SentryPrivacy.StripQuery(request.Url);
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))!.Trim();
}
