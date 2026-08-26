using System.Diagnostics;

namespace Zayra.Api.Infrastructure.Observability;

/// <summary>
/// WAVE 1 G3 — one id that ties a customer report to the logs, the trace and the job that failed.
///
/// <para>W3C <c>traceparent</c> already flows automatically through ASP.NET Core's instrumentation, but
/// a trace id is not something a person can quote down a phone line, and it is not what a support
/// engineer pastes into a log query. <c>X-Correlation-ID</c> is: accepted when the caller supplies one
/// (so a frontend request and its backend work share it), generated when they do not, echoed on the
/// response so it can be read off the network tab, and attached to the log scope and the current
/// Activity so every downstream record carries it.</para>
///
/// <para>The value is treated as UNTRUSTED INPUT. It arrives from the internet and ends up in log
/// files, span attributes and dashboards, so it is length-capped and character-restricted before it is
/// used. Without that it is a log-injection and dashboard-poisoning vector — a newline in a correlation
/// id forges log lines, and a few kilobytes of it bloats every record of the request.</para>
/// </summary>
public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-ID";
    public const string ItemsKey = "__zayra.correlation_id";

    /// <summary>
    /// Long enough for a UUID or a caller's own request id; short enough that it cannot bloat every
    /// log record of the request.
    /// </summary>
    private const int MaxLength = 64;

    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = Sanitize(context.Request.Headers[HeaderName].FirstOrDefault())
                            ?? Activity.Current?.TraceId.ToString()
                            ?? Guid.NewGuid().ToString("n");

        context.Items[ItemsKey] = correlationId;
        Activity.Current?.SetTag(ZayraTelemetry.Attr.CorrelationId, correlationId);

        // Echoed BEFORE the pipeline continues. Setting it on the way out would miss every response
        // written by a downstream component that has already started the body — including the error
        // responses that most need to be correlatable.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (_logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["TraceId"] = Activity.Current?.TraceId.ToString() ?? string.Empty,
        }))
        {
            await _next(context);
        }
    }

    /// <summary>
    /// Accepts only what is safe to write into a log line, a span attribute and a dashboard: letters,
    /// digits, dash, underscore. Anything else — a newline, a control character, an oversized blob —
    /// is rejected outright and a fresh id is generated, rather than being escaped and kept.
    /// </summary>
    internal static string? Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        if (trimmed.Length > MaxLength) return null;
        foreach (var c in trimmed)
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
                return null;
        return trimmed;
    }
}

public static class CorrelationIdExtensions
{
    /// <summary>The current request's correlation id, for handlers that want to return it in a body.</summary>
    public static string? CorrelationId(this HttpContext context) =>
        context.Items.TryGetValue(CorrelationIdMiddleware.ItemsKey, out var v) ? v as string : null;
}
