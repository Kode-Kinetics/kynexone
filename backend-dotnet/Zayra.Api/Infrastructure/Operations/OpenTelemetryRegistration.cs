using System.Runtime.CompilerServices;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Zayra.Api.Infrastructure.Operations;

/// <summary>
/// OpenTelemetry pipeline, reached only through <see cref="Observability"/> when
/// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set. Isolated in its own class so that no OpenTelemetry
/// type appears in <see cref="Observability"/>'s IL or closure fields (ObservabilityTests asserts it).
/// </summary>
internal static class OpenTelemetryRegistration
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Register(IServiceCollection services) =>
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                serviceName: Observability.ServiceName, serviceVersion: BuildInfo.Commit, serviceInstanceId: Environment.MachineName))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(options =>
                    // Health probes run every few seconds per instance; they are noise, not traffic.
                    options.Filter = context => !context.Request.Path.StartsWithSegments("/health"))
                // Outbound URLs carry personal data (Qiwa: the employee's national ID in the path) and secrets
                // (a tenant-configured device URL's query). The span keeps only the templated route.
                .AddHttpClientInstrumentation(options => options.EnrichWithHttpRequestMessage = RedactUrl)
                .AddSource(Observability.NpgsqlSource)
                .AddOtlpExporter())
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(Observability.NpgsqlSource)
                .AddOtlpExporter())
            .WithLogging(logging => logging.AddOtlpExporter(), options =>
            {
                options.IncludeScopes = true;
                options.IncludeFormattedMessage = true;
            });

    /// <summary>Overwrites the URL tags the HttpClient instrumentation set when the span started.</summary>
    internal static void RedactUrl(System.Diagnostics.Activity activity, HttpRequestMessage request)
    {
        var redacted = OutboundUrlRedactor.Redact(request.RequestUri);
        activity.SetTag("url.full", redacted);
        activity.SetTag("url.path", OutboundUrlRedactor.TemplatePath(request.RequestUri?.AbsolutePath));
        activity.SetTag("url.query", null);
        activity.SetTag("http.url", null); // pre-1.0 semantic-convention name, in case a schema switch re-emits it
    }
}
