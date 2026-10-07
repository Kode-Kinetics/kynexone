using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.Employees;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Auth;
using Zayra.Api.Infrastructure.Operations;
using Zayra.Api.Infrastructure.Qiwa;
using Zayra.Api.Models;

namespace Zayra.Api.Tests.Security;

/// <summary>
/// Identity and banking numbers stay out of what is kept or shipped beside the record: the migration batch's
/// stored package, Qiwa sync-log bodies (stored and served), and outbound HTTP spans.
/// </summary>
public class SensitivePayloadAndTelemetryTests
{
    private const string Iban = "SA0380000000608010167519";
    private const string NationalId = "1012345678";
    private const string Iqama = "2098765432";

    // ── Classification ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("IBAN Number")]
    [InlineData("Iqama No")]
    [InlineData("National ID")]
    [InlineData("Bank Account No")]
    [InlineData("bank_account_number")]
    [InlineData("Passport No.")]
    [InlineData("IqamaNumber")]
    public void IdentifierNames_AreRecognisedWhateverTheirSpelling(string name) =>
        SensitiveFieldClassifier.IsIdentifierName(name).Should().BeTrue();

    [Theory]
    [InlineData("Passport Expiry Date")]
    [InlineData("ID Type")]
    [InlineData("Designation")]
    [InlineData("EmployeeCode")]
    [InlineData("NoticePeriodDays")]
    public void OrdinaryNames_AreNot(string name) =>
        SensitiveFieldClassifier.IsIdentifierName(name).Should().BeFalse();

    [Theory]
    [InlineData(Iban, true)]
    [InlineData("SA03 8000 0000 6080 1016 7519", true)]
    [InlineData("SA0480000000608010167519", true)] // a bad checksum is still an account number
    [InlineData(NationalId, true)]
    [InlineData(Iqama, true)]
    [InlineData("3012345678", false)]
    [InlineData("101234567", false)]
    [InlineData("EMP-001", false)]
    [InlineData("2026-01-01", false)]
    public void ValueShape_CatchesIbansAndSaudiIds(string value, bool sensitive) =>
        SensitiveFieldClassifier.LooksSensitive(value).Should().Be(sensitive);

    [Fact]
    public void SanitizeFieldValue_MasksByNormalisedNameAndByValueShape()
    {
        EmployeeSafeSnapshot.SanitizeFieldValue("IBAN Number", Iban).Should().Be("***7519");
        EmployeeSafeSnapshot.SanitizeFieldValue("Iqama No", Iqama).Should().Be("***5432");
        EmployeeSafeSnapshot.SanitizeFieldValue("LegacyRef", NationalId).Should().Be("***5678", "the value is an ID whatever the column is called");
        EmployeeSafeSnapshot.SanitizeFieldValue("Notes", $"moved from {Iban} to cash").Should().Be("moved from ***7519 to cash");
        EmployeeSafeSnapshot.SanitizeFieldValue("Designation", "Manager").Should().Be("Manager");
    }

    // ── migration_import_batches.payload_json ───────────────────────────────────────────────────

    [Fact]
    public async Task MigrationBatch_StoresChecksumCountsAndAMaskedCopy_NeverTheRawPackage()
    {
        await using var db = InMemory();
        var tenantId = Guid.NewGuid();
        db.Employees.Add(new Employee { TenantId = tenantId, EmployeeCode = "EMP-001", FullName = "Imported", Status = EmployeeStatuses.Active });
        await db.SaveChangesAsync();
        var controller = MigrationController(db, tenantId);
        var request = new MigrationPackageRequest("payload-mask-001", new Dictionary<string, string>
        {
            ["employeeHistory"] =
                "EmployeeCode,EventType,FieldName,OldValue,NewValue,EffectiveDate,Reason,SourceSystem,SourceRecordId\n" +
                $"EMP-001,BankChange,IBAN Number,{Iban},SA4420000001234567891234,2026-01-01,Legacy bank change,Workday,HIST-1\n" +
                $"EMP-001,IdChange,Legacy Ref,{NationalId},{Iqama},2026-01-02,\"Renewed, see {Iqama}\",Workday,HIST-2\n" +
                "EMP-001,PayChange,Basic Salary,18000,21000,2026-01-03,Annual review,Workday,HIST-3\n" +
                "EMP-001,JobChange,Designation,Associate,Manager,2026-01-04,Promotion,Workday,HIST-4\n",
        }, false);

        var preview = await controller.Preview(request with { DryRun = true }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(preview.Result);
        var previewed = await db.MigrationImportBatches.AsNoTracking().SingleAsync(b => b.TenantId == tenantId);
        var commit = await controller.Commit(request, CancellationToken.None);
        Assert.Equal("Completed", Assert.IsType<MigrationReconciliationDto>(Assert.IsType<OkObjectResult>(commit.Result).Value).Status);
        var committed = await db.MigrationImportBatches.AsNoTracking().SingleAsync(b => b.TenantId == tenantId);

        foreach (var batch in new[] { previewed, committed }) // Preview's write (:132) and Commit's (:215)
        {
            var payload = batch.PayloadJson;
            payload.Should().NotContain(Iban).And.NotContain("SA4420000001234567891234")
                .And.NotContain(NationalId).And.NotContain(Iqama)
                .And.NotContain("18000").And.NotContain("21000");
            using var doc = JsonDocument.Parse(payload);
            doc.RootElement.GetProperty("policy").GetString().Should().Be(MigrationPackageAuditCopy.Policy);
            doc.RootElement.GetProperty("checksum").GetString().Should().Be(batch.PackageChecksum);
            doc.RootElement.GetProperty("rowCounts").GetProperty("employeeHistory").GetInt32().Should().Be(4);
            var copy = doc.RootElement.GetProperty("sections").GetProperty("employeeHistory").GetString()!;
            copy.Should().Contain("***7519").And.Contain("***1234").And.Contain("***5678").And.Contain("***5432")
                .And.Contain("\"Renewed, see ***5432\"").And.Contain("Associate,Manager").And.Contain(SensitiveValueMask.Redacted);
        }
    }

    [Fact]
    public async Task MigrationResume_NeedsOnlyThePackageTheCallerResends()
    {
        // Resume never reads payload_json: it matches the re-sent package on the checksum. A batch whose stored
        // copy is masked therefore resumes exactly as before.
        await using var db = InMemory();
        var tenantId = Guid.NewGuid();
        db.Employees.Add(new Employee { TenantId = tenantId, EmployeeCode = "EMP-001", FullName = "Imported", Status = EmployeeStatuses.Active });
        await db.SaveChangesAsync();
        var request = new MigrationPackageRequest(null, new Dictionary<string, string>
        {
            ["employeeHistory"] = "EmployeeCode,EventType,FieldName,OldValue,NewValue,EffectiveDate,Reason,SourceSystem,SourceRecordId\n" +
                                  $"EMP-001,BankChange,IBAN,{Iban},SA4420000001234567891234,2026-01-01,Legacy,Workday,HIST-1\n",
        }, false);
        var batch = new MigrationImportBatch
        {
            TenantId = tenantId, PackageType = "MigrationPackage", Status = "Failed",
            PackageChecksum = MigrationImportController.PackageChecksum(request),
            PayloadJson = MigrationPackageAuditCopy.Serialize(MigrationImportController.PackageChecksum(request), request.Sections),
        };
        db.MigrationImportBatches.Add(batch);
        await db.SaveChangesAsync();

        var resumed = await MigrationController(db, tenantId).Resume(batch.Id, request, CancellationToken.None);

        Assert.Equal("Completed", Assert.IsType<MigrationReconciliationDto>(Assert.IsType<OkObjectResult>(resumed.Result).Value).Status);
        (await db.MigrationImportBatches.SingleAsync(b => b.Id == batch.Id)).PayloadJson.Should().NotContain(Iban);
    }

    // ── Qiwa sync-log bodies ────────────────────────────────────────────────────────────────────

    [Fact]
    public void QiwaScrubber_KeepsStatusFields_DropsTheEchoedRecord_AndKeepsTheSimulationMarker()
    {
        var live = QiwaResponseScrubber.Scrub(
            $"{{\"status\":\"synced\",\"request_id\":\"Q-1\",\"id_number\":\"{NationalId}\",\"employee\":{{\"iban\":\"{Iban}\"}},\"message\":\"accepted {Iqama}\"}}")!;
        live.Should().Contain("\"status\":\"synced\"").And.Contain("\"request_id\":\"Q-1\"").And.Contain("***5432")
            .And.NotContain(NationalId).And.NotContain(Iban).And.NotContain(Iqama).And.NotContain("id_number");

        var sandbox = QiwaResponseScrubber.Scrub(
            "{\"status\":\"simulated\"," + SandboxQiwaApiAdapter.SimulationMarker + ",\"filed_with_qiwa\":false,\"adapter\":\"sandbox\"}");
        QiwaSyncLogStatuses.IsSimulated(QiwaSyncLogStatuses.Success, sandbox).Should().BeTrue("the only reader of the column is this marker");

        QiwaResponseScrubber.Scrub("<html>" + NationalId + "</html>").Should().NotContain(NationalId);
    }

    [Fact]
    public async Task QiwaSyncLogApi_ReturnsNoStoredBodies()
    {
        await using var db = InMemory();
        var tenantId = Guid.NewGuid();
        db.QiwaSyncLogs.Add(new QiwaSyncLog
        {
            TenantId = tenantId, EmployeeId = 1, Status = QiwaSyncLogStatuses.Failed,
            // A row written before the scrubber: the raw echo of the employee's record.
            RequestPayloadJson = $"{{\"id_number\":\"{NationalId}\"}}",
            ResponsePayloadJson = $"{{\"status\":\"rejected\",\"id_number\":\"{NationalId}\"}}",
        });
        await db.SaveChangesAsync();
        var controller = new QiwaController(
            new QiwaIntegrationService(db, NullLogger<QiwaIntegrationService>.Instance, DataProtectionProvider.Create("s1")),
            new SandboxQiwaApiAdapter(NullLogger<SandboxQiwaApiAdapter>.Instance))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim("tenant_id", tenantId.ToString()), new Claim("permission", "qiwa.read")], "Test")),
                },
            },
        };

        var json = JsonSerializer.Serialize(((OkObjectResult)await controller.GetSyncLogs(null, 1, 25, CancellationToken.None)).Value);

        json.Should().NotContain(NationalId).And.NotContain("PayloadJson");
        json.Should().Contain("\"Status\":\"Failed\"", "the row itself is still listed");
    }

    // ── Outbound HTTP spans ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://api.qiwa.tech/api/v1/establishments/7000123456/employees/1012345678",
                "https://api.qiwa.tech/api/v1/establishments/{id}/employees/{id}")]
    [InlineData("https://api.qiwa.tech/api/v1/establishments/EST-ABC/employees",
                "https://api.qiwa.tech/api/v1/establishments/{id}/employees")]
    [InlineData("http://10.0.0.5:8080/punches?apiKey=s3cret&employee=2098765432",
                "http://10.0.0.5:8080/punches")]
    [InlineData("https://api.anthropic.com/v1/messages", "https://api.anthropic.com/v1/messages")]
    public void OutboundUrl_KeepsTheRouteOnly(string url, string expected) =>
        OutboundUrlRedactor.Redact(new Uri(url)).Should().Be(expected);

    [Fact]
    public void TheRegisteredHttpClientInstrumentation_RedactsTheSpanUrl()
    {
        var services = new ServiceCollection();
        OpenTelemetryRegistration.Register(services);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<OpenTelemetry.Instrumentation.Http.HttpClientTraceInstrumentationOptions>>()
            .Get(Options.DefaultName);
        options.EnrichWithHttpRequestMessage.Should().NotBeNull("the Qiwa path carries the employee's national ID");

        using var activity = new Activity("GET");
        const string raw = "https://api.qiwa.tech/api/v1/establishments/7000123456/employees/1012345678?x=1";
        activity.SetTag("url.full", raw);
        options.EnrichWithHttpRequestMessage!(activity, new HttpRequestMessage(HttpMethod.Get, raw));

        activity.GetTagItem("url.full").Should().Be("https://api.qiwa.tech/api/v1/establishments/{id}/employees/{id}");
        activity.GetTagItem("url.path").Should().Be("/api/v1/establishments/{id}/employees/{id}");
        activity.Tags.Select(t => t.Value).Should().NotContain(v => v != null && v.Contains("1012345678"));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static ZayraDbContext InMemory() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static MigrationImportController MigrationController(ZayraDbContext db, Guid tenantId) =>
        new(db, new Pbkdf2PasswordHasher(), new AuditService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("tenant_id", tenantId.ToString()),
                        new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                        new Claim(ClaimTypes.Role, "Admin"),
                    }, "test")),
                },
            },
        };
}
