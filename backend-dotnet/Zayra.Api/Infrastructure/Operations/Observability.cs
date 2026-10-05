using System.Diagnostics;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging.Console;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Zayra.Api.Infrastructure.Operations;

/// <summary>
/// Logging and telemetry wiring for the API.
///
/// <para><b>Console logs.</b> One JSON object per line outside Development (simple text in
/// Development). Scopes are included, so every request log carries the W3C <c>TraceId</c>/<c>SpanId</c>
/// the host already attaches, plus <c>TenantId</c> from <see cref="UseTenantLogScope"/>. Set
/// <c>Logging__Console__FormatterName=simple</c> to go back to text.</para>
///
/// <para><b>OpenTelemetry.</b> Registered ONLY when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set; with it
/// unset nothing is registered, nothing listens and nothing leaves the process, exactly as before.
/// When set: traces (ASP.NET Core, HttpClient, Npgsql), metrics (ASP.NET Core, HttpClient, .NET runtime,
/// Npgsql) and logs, exported over OTLP. The standard <c>OTEL_*</c> variables (protocol, headers,
/// <c>OTEL_SERVICE_NAME</c>, <c>OTEL_RESOURCE_ATTRIBUTES</c>) are honoured by the exporter itself.</para>
///
/// <para><b>Personal data.</b> No email, phone, IBAN, iqama/national id or message body is logged
/// (<c>PersonalDataLoggingRatchetTests</c>). Query strings are redacted by the instrumentation's
/// defaults; Npgsql spans carry the parameterised SQL text, never parameter values; exceptions are not
/// recorded on spans, because provider exception messages (SMTP especially) can quote a recipient.</para>
/// </summary>
public static class Observability
{
    public const string ServiceName = "kynexone-api";
    public const string OtlpEndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";
    public const string TenantTag = "kynexone.tenant_id";

    /// <summary>Npgsql 8 publishes both under this name; it is what Npgsql.OpenTelemetry's AddNpgsql subscribes to.</summary>
    public const string NpgsqlSource = "Npgsql";

    public static bool IsOtlpConfigured(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration[OtlpEndpointKey]);

    public static WebApplicationBuilder AddKynexObservability(this WebApplicationBuilder builder)
    {
        ConfigureConsoleLogging(builder);

        if (!IsOtlpConfigured(builder.Configuration)) return builder;

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                serviceName: ServiceName, serviceVersion: BuildInfo.Commit, serviceInstanceId: Environment.MachineName))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(options =>
                    // Health probes run every few seconds per instance; they are noise, not traffic.
                    options.Filter = context => !context.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation()
                .AddSource(NpgsqlSource)
                .AddOtlpExporter())
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(NpgsqlSource)
                .AddOtlpExporter())
            .WithLogging(logging => logging.AddOtlpExporter(), options =>
            {
                options.IncludeScopes = true;
                options.IncludeFormattedMessage = true;
            });

        return builder;
    }

    private static void ConfigureConsoleLogging(WebApplicationBuilder builder)
    {
        var configured = builder.Configuration["Logging:Console:FormatterName"];
        var json = string.IsNullOrWhiteSpace(configured)
            ? !builder.Environment.IsDevelopment()
            : string.Equals(configured, ConsoleFormatterNames.Json, StringComparison.OrdinalIgnoreCase);
        if (!json) return;

        builder.Logging.AddJsonConsole(options =>
        {
            options.IncludeScopes = true;
            options.UseUtcTimestamp = true;
            options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
            // Keep Arabic text and punctuation readable in the log viewer rather than \uXXXX-escaped.
            options.JsonWriterOptions = new JsonWriterOptions
            {
                Indented = false,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
        });
    }

    /// <summary>
    /// After authentication: puts the caller's tenant id (a GUID, not personal data) on every log line
    /// written while the request runs, and on the request's trace span. Requests without a tenant
    /// claim (anonymous, platform audience) are left untouched.
    /// </summary>
    public static IApplicationBuilder UseTenantLogScope(this IApplicationBuilder app)
    {
        var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger("Zayra.Api.Request");
        return app.Use(async (context, next) =>
        {
            var tenantId = context.User.FindFirstValue("tenant_id");
            if (string.IsNullOrEmpty(tenantId) || !Guid.TryParse(tenantId, out _))
            {
                await next();
                return;
            }

            Activity.Current?.SetTag(TenantTag, tenantId);
            using (logger.BeginScope(new Dictionary<string, object> { ["TenantId"] = tenantId }))
                await next();
        });
    }
}
