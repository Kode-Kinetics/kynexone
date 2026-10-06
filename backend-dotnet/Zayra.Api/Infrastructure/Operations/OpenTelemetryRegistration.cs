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
                .AddHttpClientInstrumentation()
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
}
