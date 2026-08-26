using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Zayra.Api.Data;
using Zayra.Api.Controllers.Localization;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Http;
using Zayra.Api.Infrastructure.Localization;
using Zayra.Api.Infrastructure.Operations;
using Zayra.Api.Infrastructure.OpenApi;
using Zayra.Api.Models;
using Xunit;

namespace Zayra.Api.Tests;

// P0-4/P0-5/P0-6 + headers — pure unit tests (no host, no DB).
public class ProductionHardeningUnitTests
{
    // ── P0-5: storage fail-fast + selection ─────────────────────────────────────

    private static IConfiguration Config(params (string Key, string Value)[] kv)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(kv.ToDictionary(x => x.Key, x => (string?)x.Value)).Build();

    [Fact]
    public void Storage_Production_WithoutS3_FailsFast()
    {
        var act = () => DocumentStorageRegistration.ResolveAndValidate(Config(), isDevelopment: false);
        act.Should().Throw<InvalidOperationException>().WithMessage("*durable object storage*");
    }

    [Fact]
    public void Storage_Production_EphemeralEscapeHatchStillFailsClosed()
    {
        var cfg = Config(("Storage:Provider", "local"), ("Storage:AllowEphemeral", "true"));
        var act = () => DocumentStorageRegistration.ResolveAndValidate(cfg, isDevelopment: false);
        act.Should().Throw<InvalidOperationException>().WithMessage("*durable object storage*");
    }

    [Fact]
    public void Storage_Production_S3ButNoBucket_FailsFast()
    {
        var cfg = Config(("Storage:Provider", "s3"));
        var act = () => DocumentStorageRegistration.ResolveAndValidate(cfg, isDevelopment: false);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Bucket/AccessKey/SecretKey*");
    }

    [Fact]
    public void Storage_Development_LocalIsAllowed()
    {
        var opts = DocumentStorageRegistration.ResolveAndValidate(Config(), isDevelopment: true);
        opts.Provider.Should().Be("local");
    }

    [Fact]
    public void Storage_Production_S3FullyConfigured_RegistersS3()
    {
        var cfg = Config(("Storage:Provider", "s3"), ("Storage:Bucket", "b"),
            ("Storage:AccessKey", "ak"), ("Storage:SecretKey", "sk"), ("Storage:Region", "me-central-1"));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDocumentStorage(cfg, isDevelopment: false);
        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<IDocumentStorage>().Should().BeOfType<S3DocumentStorage>();
    }

    // ── P0-4: readiness status rule ─────────────────────────────────────────────

    [Theory]
    [InlineData(true, 0, "ready")]
    [InlineData(true, 1, "not_ready")]
    [InlineData(true, 5, "not_ready")]
    [InlineData(false, 0, "not_ready")]
    [InlineData(true, -1, "not_ready")]
    public void ReadinessStatus_ReadyOnlyWhenHealthyAndNoPendingMigrations(bool healthy, int pending, string expected)
        => ProductionReadinessEvidence.ResolveStatus(healthy, pending).Should().Be(expected);

    [Fact]
    public void WorkerReadiness_StaleCriticalHeartbeat_FailsClosed()
    {
        var now = DateTime.UtcNow;
        var rows = ProductionWorkerNames.All.Select(name => new WorkerHeartbeat
        {
            WorkerName = name,
            InstanceId = "instance-1",
            Status = WorkerHeartbeatStatuses.Healthy,
            LastSucceededAtUtc = now,
            UpdatedAtUtc = name == ProductionWorkerNames.Qiwa ? now.AddMinutes(-4) : now
        }).ToList();

        var workers = ProductionReadinessEvidence.EvaluateWorkers(rows, now);

        workers.Healthy.Should().BeFalse();
        workers.StaleCount.Should().Be(1);
        workers.Workers.Single(x => x.Name == ProductionWorkerNames.Qiwa).Status.Should().Be("stale");
        ProductionReadinessEvidence.ResolveStatus(true, 0, workers.Healthy).Should().Be("not_ready");
    }

    [Fact]
    public void RuntimeAssembly_ContainsEfMigrationsRequiredByReadinessGate()
    {
        var options = new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .Options;
        using var db = new ZayraDbContext(options);

        db.Database.GetMigrations().Should().NotBeEmpty(
            "the production image must retain migration metadata for /health/ready parity checks");
    }

    // ── Headers: CSP/HSTS/Permissions-Policy present; /swagger exempt from CSP ───

    [Fact]
    public void SecurityHeaders_ApiResponse_HasCspHstsAndPermissionsPolicy()
    {
        var headers = new HeaderDictionary();
        SecurityHeaders.Apply(headers, "/api/employees");

        headers["Content-Security-Policy"].ToString().Should().Contain("frame-ancestors 'none'").And.Contain("script-src 'self'");
        headers["Strict-Transport-Security"].ToString().Should().Contain("max-age=");
        headers["Permissions-Policy"].ToString().Should().Contain("geolocation=()");
        headers["Cache-Control"].ToString().Should().Contain("no-store"); // payroll/employees never cached
    }

    [Fact]
    public void SecurityHeaders_SwaggerPath_ExemptFromCsp_ButKeepsHsts()
    {
        var headers = new HeaderDictionary();
        SecurityHeaders.Apply(headers, "/swagger/index.html");
        headers.ContainsKey("Content-Security-Policy").Should().BeFalse("Swagger's inline bootstrap needs the CSP exemption in dev");
        headers.ContainsKey("Strict-Transport-Security").Should().BeTrue();
    }

    [Fact]
    public void SwaggerSchemaIds_DuplicateShortDtoNames_RemainUnique()
    {
        SwaggerSchemaIds.For(typeof(Zayra.Api.Application.Attendance.RegularizationDecisionRequest))
            .Should().NotBe(SwaggerSchemaIds.For(typeof(Zayra.Api.Controllers.Leave.RegularizationDecisionRequest)));
    }

    // ── P0-6: offline transliteration (no network) ──────────────────────────────

    [Fact]
    public void Transliteration_IsOfflineDeterministicAndArabic()
    {
        var svc = new TransliterationService();
        svc.ToArabic("").Should().BeEmpty();

        var a = svc.ToArabic("Mohammed Rashid");
        var b = svc.ToArabic("Mohammed Rashid");
        a.Should().Be(b, "transliteration must be deterministic");
        a.Should().NotBeNullOrWhiteSpace();
        a.Should().MatchRegex("[؀-ۿ]", "output must contain Arabic-script characters");
        a.Should().Contain("ش", "the 'sh' digraph in Rashid must map to ش");
        a.Should().NotMatchRegex("[a-zA-Z]", "no Latin letters may leak into the Arabic suggestion");
    }

    [Fact]
    public void TransliterationController_RequiresTenant_AndReturnsSuggestion()
    {
        var ctrl = new TransliterationController(new TransliterationService());

        // No tenant claim → Unauthorized.
        ctrl.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        ctrl.Transliterate(new TransliterateRequest("Ali", "ar")).Should().BeOfType<UnauthorizedResult>();

        // With tenant claim → Ok with a suggestion, and no-store cache header.
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("tenant_id", Guid.NewGuid().ToString()) }, "Test"));
        ctrl.ControllerContext = new ControllerContext { HttpContext = http };
        var ok = ctrl.Transliterate(new TransliterateRequest("Ali", "ar")).Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().NotBeNull();
        http.Response.Headers["Cache-Control"].ToString().Should().Contain("no-store");
    }
}
