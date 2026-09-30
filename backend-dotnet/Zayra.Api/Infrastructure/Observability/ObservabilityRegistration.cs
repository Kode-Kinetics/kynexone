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
        var hasCollector = IsUsableOtlpEndpoint(endpoint);

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
                        //
                        // AND NEITHER IS AN EXCEPTION MESSAGE. RecordException = true makes the SDK
                        // attach an `exception` event carrying exception.message and
                        // exception.stacktrace verbatim, and this codebase interpolates PII straight
                        // into those messages -- EmployeeManagementService throws
                        // $"IBAN '{cleanIban}' is invalid ..." and
                        // $"Salary package {grossSalary:N2} is below grade {grade.Code} minimum ...".
                        // Exporting them ships IBANs and salary packages to whatever collector
                        // Observability:OtlpEndpoint happens to name, where they are retained for
                        // months and read by people who would never be granted access to the payroll
                        // database. Under PDPL that is a disclosure, and it is one a config change
                        // alone would trigger -- no code review required.
                        //
                        // REJECTED ALTERNATIVE: keep RecordException = true and scrub the message in
                        // EnrichWithException. It cannot work. The SDK has already recorded the
                        // exception event by the time enrichment runs, and an ActivityEvent is
                        // immutable once added -- Activity exposes no API to remove or rewrite one.
                        // The only way to not export a message is to never record it.
                        //
                        // What replaces it is the pattern WorkerHeartbeatReporter already uses on
                        // main -- exception.GetType().Name. The fact of the failure and its class are
                        // preserved (an InvalidOperationException is still visibly an
                        // InvalidOperationException on the span, and the span status is still Error);
                        // only the operator-authored text, which is the part that carries the data,
                        // is dropped.
                        o.RecordException = false;
                        o.EnrichWithException = (activity, exception) =>
                            activity.SetTag(ZayraTelemetry.Attr.ExceptionType, exception.GetType().Name);

                        o.EnrichWithHttpRequest = (activity, request) =>
                        {
                            var correlationId = request.HttpContext.CorrelationId();
                            if (correlationId is not null)
                                activity.SetTag(ZayraTelemetry.Attr.CorrelationId, correlationId);
                        };
                    })
                    .AddHttpClientInstrumentation(o =>
                    {
                        // Same rule outbound. A failed call to a bank, GOSI or Qiwa endpoint raises an
                        // exception whose message routinely quotes the request that failed.
                        o.RecordException = false;
                        o.EnrichWithException = (activity, exception) =>
                            activity.SetTag(ZayraTelemetry.Attr.ExceptionType, exception.GetType().Name);
                    });

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

    /// <summary>
    /// An OTLP endpoint is usable only if it is an absolute URI whose scheme is http or https.
    ///
    /// <para>The scheme check is not pedantry. <c>Uri.TryCreate("localhost:4317", Absolute, out _)</c>
    /// SUCCEEDS — it parses as scheme <c>localhost</c> with path <c>4317</c> — and
    /// <c>localhost:4317</c> is the single most common way a person writes an OTLP endpoint. Accepting
    /// it registers an exporter that can never connect: the OTLP exporter then retries on a background
    /// thread forever, floods the log with transport errors and adds latency to the very requests the
    /// tracing is meant to observe. That is exactly the failure mode this class's remarks promise to
    /// avoid, and without this check a one-character configuration slip walks straight into it.</para>
    ///
    /// <para>Rejecting is the safe direction: an unusable endpoint degrades to the documented no-op
    /// (instrumentation runs, nothing is shipped) rather than to a broken exporter.</para>
    /// </summary>
    internal static bool IsUsableOtlpEndpoint(string? endpoint) =>
        !string.IsNullOrWhiteSpace(endpoint)
        && Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
