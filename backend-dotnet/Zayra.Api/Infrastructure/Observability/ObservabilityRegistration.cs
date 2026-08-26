using Microsoft.AspNetCore.Http;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Zayra.Api.Infrastructure.Observability;

/// <summary>Configuration for the observability stack. Bound from <c>Observability:*</c>.</summary>
public sealed class ObservabilityOptions
{
    /// <summary>OTLP endpoint. When empty, NOTHING is exported — see the class remarks below.</summary>
    public string? OtlpEndpoint { get; set; }

    /// <summary>Head sampling ratio, 0..1. Defaults to everything outside Production.</summary>
    public double? SamplingRatio { get; set; }

    /// <summary>Deployment name surfaced as a resource attribute.</summary>
    public string? Environment { get; set; }

    /// <summary>Service version — the deployed commit, when the platform provides one.</summary>
    public string? ServiceVersion { get; set; }
}

/// <summary>
/// WAVE 1 G3 — provider-neutral OpenTelemetry wiring.
///
/// <para><b>No vendor is hardcoded.</b> Everything ships over OTLP to whatever
/// <c>Observability:OtlpEndpoint</c> names — a local collector, Grafana, Honeycomb, Datadog's OTLP
/// intake. Changing backend is a configuration change, not a code change.</para>
///
/// <para><b>Unconfigured is a genuine no-op, not a broken export.</b> If no endpoint is set, no
/// exporter is registered at all. That matters more than it sounds: an OTLP exporter pointed at
/// nothing retries on a background thread, fills the log with connection errors, and adds latency to
/// the very requests you are trying to observe. Instrumentation still runs, so <c>Activity.Current</c>
/// and the correlation id remain available for logging — the data is collected and simply not shipped.</para>
/// </summary>
public static class ObservabilityRegistration
{
    public static IServiceCollection AddZayraObservability(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.Configure<ObservabilityOptions>(configuration.GetSection("Observability"));

        var options = configuration.GetSection("Observability").Get<ObservabilityOptions>() ?? new();
        var endpoint = options.OtlpEndpoint;
        var hasCollector = !string.IsNullOrWhiteSpace(endpoint) && Uri.TryCreate(endpoint, UriKind.Absolute, out _);

        // Production defaults to 10% head sampling; elsewhere everything, because a developer chasing
        // one request should not have to hope it was sampled.
        var ratio = options.SamplingRatio ?? (environment.IsProduction() ? 0.1 : 1.0);
        ratio = Math.Clamp(ratio, 0d, 1d);

        var resource = ResourceBuilder.CreateDefault()
            .AddService(
                serviceName: ZayraTelemetry.ServiceName,
                serviceVersion: options.ServiceVersion
                                ?? Environment.GetEnvironmentVariable("RENDER_GIT_COMMIT")
                                ?? "local")
            .AddAttributes(new KeyValuePair<string, object>[]
            {
                new("deployment.environment", options.Environment ?? environment.EnvironmentName),
            });

        services.AddOpenTelemetry()
            .WithTracing(tracing =>
            {
                tracing
                    .SetResourceBuilder(resource)
                    .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(ratio)))
                    .AddSource(ZayraTelemetry.ServiceName)
                    .AddAspNetCoreInstrumentation(o =>
                    {
                        // Health probes run every few seconds forever and would swamp the trace budget
                        // while telling nobody anything.
                        o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health");

                        // Request and response BODIES are never captured. On this product a body is a
                        // payroll payload, a bank file or an employee record.
                        o.RecordException = true;
                        o.EnrichWithHttpRequest = (activity, request) =>
                        {
                            var correlationId = request.HttpContext.CorrelationId();
                            if (correlationId is not null)
                                activity.SetTag(ZayraTelemetry.Attr.CorrelationId, correlationId);
                        };
                    })
                    .AddHttpClientInstrumentation(o => o.RecordException = true);

                // EF CORE INSTRUMENTATION IS DELIBERATELY ABSENT — see GAP-G3-1.
                // The package is prerelease-only, and between versions it REMOVED the
                // SetDbStatementForText / SetDbStatementForStoredProcedure switches that turn off SQL
                // capture. On this schema a captured statement carries salaries, IBANs and identity
                // numbers, so taking a beta dependency whose redaction behaviour cannot be pinned is
                // the wrong trade for query timings. Database latency is covered by the domain
                // operation histogram until a stable release restores an explicit redaction switch.

                if (hasCollector) tracing.AddOtlpExporter(o => o.Endpoint = new Uri(endpoint!));
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .SetResourceBuilder(resource)
                    .AddMeter(ZayraTelemetry.ServiceName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();

                if (hasCollector) metrics.AddOtlpExporter(o => o.Endpoint = new Uri(endpoint!));
            });

        return services;
    }
}
