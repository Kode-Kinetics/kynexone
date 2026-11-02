using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sentry;
using Zayra.Api.Infrastructure.Operations;

namespace Zayra.Api.Tests;

/// <summary>
/// Error tracking is dormant without a DSN, and what it sends is scrubbed in one place
/// (<see cref="SentryRegistration"/> over <see cref="SentryPrivacy"/>). The sample values below are fakes.
/// </summary>
public sealed class SentryErrorTrackingTests
{
    private const string Iban = "SA0380000000608010167519";
    private const string NationalId = "1087654321";
    private const string Iqama = "2123456789";
    private const string Email = "first.last@example-corp.sa";
    private const string Salary = "15750.50";
    // A fake token assembled at runtime so secret scanners do not flag the fixture.
    private static readonly string Bearer = string.Join(".", "eyJhbGciOiJIUzI1NiJ9", "eyJzdWIiOiIxMjM0NTY3ODkwIn0", "c2lnbmF0dXJlLXZhbHVl");
    private static readonly string[] Secrets = [Iban, NationalId, Iqama, Email, Salary, Bearer, "first.last"];

    private static string PayrollUrl => $"https://api.example.test/api/payroll/runs?employee={NationalId}&iban={Iban}&salary={Salary}";

    [Fact]
    public void ScrubText_RemovesEverySensitiveShape()
    {
        var text = $"failed for {Email} iban {Iban} id {NationalId} iqama {Iqama} Authorization: Bearer {Bearer} at {PayrollUrl}";
        var scrubbed = SentryPrivacy.ScrubText(text);
        foreach (var secret in Secrets) scrubbed.Should().NotContain(secret);
        scrubbed.Should().NotContain("?employee");
    }

    [Fact]
    public void BeforeSend_LeavesNoSensitiveValueAnywhereOnTheEvent()
    {
        var evt = new SentryEvent(new InvalidOperationException($"bad row for {Email} iban {Iban}"))
        {
            Message = new SentryMessage { Message = $"salary {Salary} id {NationalId} Bearer {Bearer}", Formatted = $"iqama {Iqama}" },
            ServerName = "host-1",
        };
        evt.Request.Url = PayrollUrl;
        evt.Request.QueryString = $"salary={Salary}&iban={Iban}";
        evt.Request.Cookies = $"session={Bearer}";
        evt.Request.Data = $"{{\"iban\":\"{Iban}\",\"salary\":{Salary}}}";
        evt.Request.Headers["Authorization"] = $"Bearer {Bearer}";
        evt.Request.Headers["Cookie"] = $"session={Bearer}";
        evt.Request.Env["HTTP_X_USER"] = Email;
        evt.User.Email = Email;
        evt.User.Username = "first.last";
        evt.User.IpAddress = "203.0.113.9";
        evt.SentryExceptions = [new Sentry.Protocol.SentryException { Value = $"{Email} {Iban} {NationalId}" }];

        var result = SentryRegistration.BeforeSend(evt);

        result.Should().NotBeNull();
        var everything = string.Join('\n', Flatten(result!));
        foreach (var secret in Secrets) everything.Should().NotContain(secret);
        result!.Request.Url.Should().Be("https://api.example.test/api/payroll/runs");
        result.Request.QueryString.Should().BeNull();
        result.Request.Cookies.Should().BeNull();
        result.Request.Data.Should().BeNull();
        result.Request.Headers.Should().BeEmpty();
        result.User.Email.Should().BeNull();
        result.User.Username.Should().BeNull();
        result.User.IpAddress.Should().BeNull();
    }

    [Fact]
    public void BeforeSendTransaction_DropsRequestDataAndUser()
    {
        var tx = new SentryTransaction("GET /api/payroll", "http.server");
        tx.Request.Url = PayrollUrl;
        tx.Request.Headers["Authorization"] = $"Bearer {Bearer}";
        tx.User.Email = Email;

        var result = SentryRegistration.BeforeSendTransaction(tx)!;

        result.Request.Url.Should().Be("https://api.example.test/api/payroll/runs");
        result.Request.Headers.Should().BeEmpty();
        result.User.Email.Should().BeNull();
    }

    [Fact]
    public void BeforeSend_DropsBusinessRuleErrorsTheGlobalHandlerMapsBelow500()
    {
        // ExceptionResponseMapper maps InvalidOperationException to a 4xx; it is the user's mistake, not an incident.
        var mapped = new SentryEvent(new InvalidOperationException("Leave balance too low")) { Logger = "GlobalExceptionHandler" };
        SentryRegistration.BeforeSend(mapped).Should().BeNull();

        var crash = new SentryEvent(new NullReferenceException()) { Logger = "GlobalExceptionHandler" };
        SentryRegistration.BeforeSend(crash).Should().NotBeNull();
    }

    [Theory]
    [InlineData("GET /api/payroll/runs?salary=15750.50")]
    [InlineData("employee 1087654321 loaded")]
    [InlineData("sent to first.last@example-corp.sa")]
    public void BeforeBreadcrumb_DropsCrumbsCarryingQueryStringsOrPersonalData(string message)
    {
        SentryRegistration.BeforeBreadcrumb(new Breadcrumb(message, "http")).Should().BeNull();
        SentryRegistration.BeforeBreadcrumb(new Breadcrumb("GET /api/payroll/runs", "http")).Should().NotBeNull();
    }

    [Fact]
    public void TenantTag_IsAcceptedOnlyAsAnOpaqueGuid()
    {
        var id = Guid.NewGuid();
        SentryPrivacy.TryOpaqueTenantId(id.ToString("N"), out var accepted).Should().BeTrue();
        accepted.Should().Be(id.ToString("D"));
        SentryPrivacy.TryOpaqueTenantId("Acme Payroll Ltd", out _).Should().BeFalse();
        SentryPrivacy.TryOpaqueTenantId(Email, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(null, 0d)]
    [InlineData("", 0d)]
    [InlineData("abc", 0d)]
    [InlineData("2", 0d)]
    [InlineData("-1", 0d)]
    [InlineData("0.25", 0.25d)]
    public void TracesSampleRate_DefaultsToZero(string? raw, double expected) =>
        SentryPrivacy.ParseTracesSampleRate(raw).Should().Be(expected);

    [Fact]
    public async Task WithoutADsn_SentryIsNotInitialisedAndNothingStartsUp()
    {
        await using var app = Build();
        app.Services.GetService<IHub>().Should().BeNull("no DSN means no Sentry registration at all");
    }

    [Fact]
    public async Task WithADsn_SentryIsRegistered_WithPiiOffAndTracingOff()
    {
        await using var app = Build($"--{Observability.SentryDsnKey}=https://publickey@o0.ingest.example.test/1");
        var options = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Sentry.AspNetCore.SentryAspNetCoreOptions>>().Value;
        options.SendDefaultPii.Should().BeFalse();
        options.TracesSampleRate.Should().Be(0d);
        options.MaxRequestBodySize.Should().Be(Sentry.Extensibility.RequestSize.None);
        options.CaptureFailedRequests.Should().BeFalse();
    }

    /// <summary>With the DSN unset the Sentry assemblies must not load: Observability may not name a Sentry type.</summary>
    [Fact]
    public void ObservabilityNamesNoSentryType_OnlyNoInliningBridgesReachIt()
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var observability = typeof(Observability);
        foreach (var name in new[] { "RegisterSentry", "TagSentryTenant" })
            observability.GetMethod(name, all)!.MethodImplementationFlags
                .Should().HaveFlag(MethodImplAttributes.NoInlining, $"{name} is the out-of-line bridge");

        var types = new[] { observability }.Concat(observability.GetNestedTypes(all));
        foreach (var type in types)
        {
            foreach (var field in type.GetFields(all)) IsSentry(field.FieldType).Should().BeFalse($"{type.Name}.{field.Name}");
            foreach (var method in type.GetMethods(all))
            {
                IsSentry(method.ReturnType).Should().BeFalse($"{type.Name}.{method.Name} return");
                method.GetParameters().Should().NotContain(p => IsSentry(p.ParameterType), $"{type.Name}.{method.Name} parameters");
                foreach (var local in method.GetMethodBody()?.LocalVariables ?? [])
                    IsSentry(local.LocalType).Should().BeFalse($"{type.Name}.{method.Name} local");
            }
        }
    }

    private static bool IsSentry(Type type) =>
        type.Assembly.GetName().Name?.StartsWith("Sentry", StringComparison.Ordinal) == true
        || (type.IsGenericType && type.GetGenericArguments().Any(IsSentry))
        || (type.HasElementType && IsSentry(type.GetElementType()!));

    /// <summary>Every string an event would put on the wire from the parts the scrub covers.</summary>
    private static IEnumerable<string> Flatten(SentryEvent evt)
    {
        var parts = new List<string?>
        {
            evt.Message?.Message, evt.Message?.Formatted, evt.ServerName,
            evt.Request.Url, evt.Request.QueryString, evt.Request.Cookies, evt.Request.Data?.ToString(),
            evt.User.Email, evt.User.Username, evt.User.IpAddress, evt.User.Id,
        };
        parts.AddRange(evt.Request.Headers.Select(h => $"{h.Key}={h.Value}"));
        parts.AddRange(evt.Request.Env.Select(h => $"{h.Key}={h.Value}"));
        parts.AddRange((evt.SentryExceptions ?? []).Select(e => e.Value));
        parts.AddRange(evt.Tags.Select(t => $"{t.Key}={t.Value}"));
        return parts.Where(p => p is not null)!;
    }

    private static WebApplication Build(params string[] args)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production, Args = args });
        builder.AddKynexObservability();
        return builder.Build();
    }
}
