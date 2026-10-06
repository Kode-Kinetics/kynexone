using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Zayra.Api.Infrastructure.Operations;
using Zayra.Api.Infrastructure.Qiwa;
using Zayra.Api.Tests.Security;

namespace Zayra.Api.Tests;

public sealed class ObservabilityTests
{
    [Fact]
    public async Task WithoutAnOtlpEndpoint_NothingIsRegistered()
    {
        await using var app = Build(Environments.Production);
        app.Services.GetService<TracerProvider>().Should().BeNull("no endpoint means no OpenTelemetry at all");
        app.Services.GetService<MeterProvider>().Should().BeNull();
    }

    [Fact]
    public async Task WithAnOtlpEndpoint_TracingAndMetricsAreRegistered()
    {
        await using var app = Build(Environments.Production, $"--{Observability.OtlpEndpointKey}=http://127.0.0.1:4317");
        app.Services.GetService<TracerProvider>().Should().NotBeNull();
        app.Services.GetService<MeterProvider>().Should().NotBeNull();
    }

    /// <summary>End to end: with the endpoint set, a database span reaches an OTLP/HTTP collector.</summary>
    [Fact]
    public async Task WithAnOtlpEndpoint_SpansAreExportedToTheCollector()
    {
        using var collector = new System.Net.HttpListener();
        var port = FreePort();
        collector.Prefixes.Add($"http://127.0.0.1:{port}/");
        collector.Start();
        // Metrics and logs may arrive too, in any order; answer everything until traces show up.
        var received = Task.Run(async () =>
        {
            while (true)
            {
                var context = await collector.GetContextAsync();
                var path = context.Request.Url!.AbsolutePath;
                context.Response.StatusCode = 200;
                context.Response.Close();
                if (path == "/v1/traces") return path;
            }
        });

        var app = Build(Environments.Production,
            $"--{Observability.OtlpEndpointKey}=http://127.0.0.1:{port}",
            "--OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf");
        await app.StartAsync();
        using (var source = new ActivitySource(Observability.NpgsqlSource))
        using (source.StartActivity("SELECT 1")) { }
        await app.StopAsync();
        await app.DisposeAsync(); // flushes the batch processor

        (await received.WaitAsync(TimeSpan.FromSeconds(15))).Should().Be("/v1/traces");
    }

    /// <summary>
    /// With the endpoint unset the OpenTelemetry assemblies must not even load. In-process that cannot be
    /// observed directly (this test assembly references OpenTelemetry itself), so it is proved
    /// structurally: the JIT loads an assembly when it compiles a method whose IL names one of its
    /// members or types, so no method, field or closure of <see cref="Observability"/> may name an
    /// OpenTelemetry type, and the only way out is a NoInlining call into
    /// <see cref="OpenTelemetryRegistration"/>.
    /// </summary>
    [Fact]
    public void ObservabilityNamesNoOpenTelemetryType_OnlyANoInliningCallReachesIt()
    {
        var observability = typeof(Observability);
        var types = new[] { observability }.Concat(observability.GetNestedTypes(AllMembers)).ToList();
        var offenders = new List<string>();
        foreach (var type in types)
        {
            foreach (var field in type.GetFields(AllMembers))
                if (MentionsOpenTelemetry(field.FieldType)) offenders.Add($"{type.Name}.{field.Name}: {field.FieldType}");
            foreach (var method in type.GetMethods(AllMembers).Cast<System.Reflection.MethodBase>().Concat(type.GetConstructors(AllMembers)))
            {
                if (method.DeclaringType != type) continue;
                var body = method.GetMethodBody();
                if (body is null) continue;
                foreach (var local in body.LocalVariables)
                    if (MentionsOpenTelemetry(local.LocalType)) offenders.Add($"{type.Name}.{method.Name}: local {local.LocalType}");
                foreach (var member in ReferencedMembers(method))
                    if (MentionsOpenTelemetry(member)) offenders.Add($"{type.Name}.{method.Name}: {member.DeclaringType}.{member.Name}");
            }
        }
        offenders.Should().BeEmpty("naming an OpenTelemetry type here loads its assembly even when no endpoint is set");

        var bridge = observability.GetMethod("RegisterOpenTelemetry", AllMembers)!;
        bridge.MethodImplementationFlags.Should().HaveFlag(System.Reflection.MethodImplAttributes.NoInlining);
        ReferencedMembers(observability.GetMethod(nameof(Observability.AddKynexObservability))!)
            .Should().Contain(bridge, "AddKynexObservability reaches OpenTelemetry only through the out-of-line bridge");
        ReferencedMembers(bridge).Should().Contain(typeof(OpenTelemetryRegistration).GetMethod("Register"));
        ReferencedMembers(typeof(OpenTelemetryRegistration).GetMethod("Register")!)
            .Should().Contain(m => MentionsOpenTelemetry(m), "the check above would be vacuous if it could not see OpenTelemetry references");
    }

    private const System.Reflection.BindingFlags AllMembers = System.Reflection.BindingFlags.Public
        | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance
        | System.Reflection.BindingFlags.DeclaredOnly;

    private static bool MentionsOpenTelemetry(Type? type) =>
        type is not null && (IsOpenTelemetryAssembly(type.Assembly)
            || (type.IsGenericType && type.GetGenericArguments().Any(MentionsOpenTelemetry))
            || (type.HasElementType && MentionsOpenTelemetry(type.GetElementType())));

    private static bool MentionsOpenTelemetry(System.Reflection.MemberInfo member) =>
        MentionsOpenTelemetry(member.DeclaringType)
        || (member is System.Reflection.MethodInfo m && (MentionsOpenTelemetry(m.ReturnType)
            || m.GetParameters().Any(p => MentionsOpenTelemetry(p.ParameterType))
            || (m.IsGenericMethod && m.GetGenericArguments().Any(MentionsOpenTelemetry))))
        || (member is System.Reflection.FieldInfo f && MentionsOpenTelemetry(f.FieldType))
        || (member is Type t && MentionsOpenTelemetry(t));

    private static bool IsOpenTelemetryAssembly(System.Reflection.Assembly assembly) =>
        assembly.GetName().Name?.StartsWith("OpenTelemetry", StringComparison.Ordinal) == true
        || assembly.GetName().Name == "Npgsql.OpenTelemetry";

    private static readonly Dictionary<short, System.Reflection.Emit.OpCode> OpCodesByValue =
        typeof(System.Reflection.Emit.OpCodes).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(f => (System.Reflection.Emit.OpCode)f.GetValue(null)!)
            .ToDictionary(o => o.Value);

    /// <summary>Every method, field and type token in a method's IL, resolved.</summary>
    private static List<System.Reflection.MemberInfo> ReferencedMembers(System.Reflection.MethodBase method)
    {
        var members = new List<System.Reflection.MemberInfo>();
        var il = method.GetMethodBody()?.GetILAsByteArray() ?? [];
        var typeArgs = method.DeclaringType?.IsGenericType == true ? method.DeclaringType.GetGenericArguments() : null;
        var methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
        for (var i = 0; i < il.Length;)
        {
            short value = il[i++];
            if (value == 0xFE) value = (short)(0xFE00 | il[i++]);
            var op = OpCodesByValue[value];
            switch (op.OperandType)
            {
                case System.Reflection.Emit.OperandType.InlineMethod:
                case System.Reflection.Emit.OperandType.InlineField:
                case System.Reflection.Emit.OperandType.InlineType:
                case System.Reflection.Emit.OperandType.InlineTok:
                    var token = BitConverter.ToInt32(il, i);
                    var resolved = method.Module.ResolveMember(token, typeArgs, methodArgs);
                    if (resolved is not null) members.Add(resolved);
                    i += 4;
                    break;
                case System.Reflection.Emit.OperandType.InlineNone: break;
                case System.Reflection.Emit.OperandType.ShortInlineBrTarget:
                case System.Reflection.Emit.OperandType.ShortInlineI:
                case System.Reflection.Emit.OperandType.ShortInlineVar: i += 1; break;
                case System.Reflection.Emit.OperandType.InlineVar: i += 2; break;
                case System.Reflection.Emit.OperandType.InlineI8:
                case System.Reflection.Emit.OperandType.InlineR: i += 8; break;
                case System.Reflection.Emit.OperandType.InlineSwitch: i += 4 + 4 * BitConverter.ToInt32(il, i); break;
                default: i += 4; break;
            }
        }
        return members;
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Production today: the real Program.cs composition, no endpoint, so no OpenTelemetry.</summary>
    [Fact]
    public async Task ProgramComposition_WithoutEndpoint_RegistersNoTelemetryPipeline()
    {
        var connectionString = $"Data Source=file:otel-{Guid.NewGuid():N}?mode=memory&cache=shared";
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        await using var host = new AuthorizationPipelineHost(connectionString);
        _ = host.CreateClient();
        host.Services.GetService<TracerProvider>().Should().BeNull();
    }

    [Fact]
    public async Task ConsoleLogsAreJsonOutsideDevelopment_TextInDevelopment_AndOverridable()
    {
        await using (var prod = Build(Environments.Production))
            FormatterName(prod).Should().Be(ConsoleFormatterNames.Json);
        await using (var dev = Build(Environments.Development))
            FormatterName(dev).Should().NotBe(ConsoleFormatterNames.Json);
        await using (var optedOut = Build(Environments.Production, "--Logging:Console:FormatterName=simple"))
            FormatterName(optedOut).Should().NotBe(ConsoleFormatterNames.Json);
    }

    [Fact]
    public async Task RequestLogsCarryTenantIdAndTraceId_AndTheSpanCarriesTheTenant()
    {
        var tenantId = Guid.NewGuid().ToString();
        var capture = new ScopeCapturingProvider();
        Activity? requestActivity = null;

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(capture);
        builder.AddKynexObservability();
        await using var app = builder.Build();
        // Stand-in for UseAuthentication: a validated principal with the tenant claim.
        app.Use((context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant_id", tenantId)], "test"));
            return next();
        });
        app.UseTenantLogScope();
        app.MapGet("/probe", (ILogger<ObservabilityTests> log) =>
        {
            requestActivity = Activity.Current;
            log.LogInformation("probe handled");
            return Results.Ok();
        });
        await app.StartAsync();

        // No ActivityListener and no OpenTelemetry: the production default. The host still creates the
        // request Activity (its own diagnostics logger is enabled), so TraceId is in scope regardless.
        var response = await app.GetTestClient().GetAsync("/probe");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var entry = capture.Entries.Single(e => e.Message == "probe handled");
        entry.Scope.Should().ContainKey("TenantId").WhoseValue.Should().Be(tenantId);
        entry.Scope.Should().ContainKey("TraceId");
        requestActivity.Should().NotBeNull();
        requestActivity!.GetTagItem(Observability.TenantTag).Should().Be(tenantId);
        await app.StopAsync();
    }

    [Theory]
    [InlineData("""{"error":"invalid_client","error_description":"client 1234 for ops@example.com"}""", "invalid_client")]
    [InlineData("""{"error":"x@y.com"}""", "unparsed")]
    [InlineData("<html>upstream</html>", "unparsed")]
    public void QiwaTokenFailureLogsOnlyTheOAuthErrorCode(string body, string expected) =>
        LiveQiwaApiAdapter.OAuthErrorCode(body).Should().Be(expected);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static WebApplication Build(string environment, params string[] args)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment, Args = args });
        builder.AddKynexObservability();
        return builder.Build();
    }

    private static string? FormatterName(WebApplication app) =>
        app.Services.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>().CurrentValue.FormatterName;

    private sealed record Entry(string Message, IReadOnlyDictionary<string, object?> Scope);

    private sealed class ScopeCapturingProvider : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();
        public List<Entry> Entries { get; } = [];
        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);
        public void Dispose() { }

        private sealed class CapturingLogger(ScopeCapturingProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes.Push(state);
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var scope = new Dictionary<string, object?>();
                owner._scopes.ForEachScope((s, acc) =>
                {
                    if (s is IEnumerable<KeyValuePair<string, object?>> pairs)
                        foreach (var (k, v) in pairs) acc[k] = v?.ToString();
                    else if (s is IEnumerable<KeyValuePair<string, object>> pairs2)
                        foreach (var (k, v) in pairs2) acc[k] = v?.ToString();
                }, scope);
                lock (owner.Entries) owner.Entries.Add(new Entry(formatter(state, exception), scope));
            }
        }
    }
}
