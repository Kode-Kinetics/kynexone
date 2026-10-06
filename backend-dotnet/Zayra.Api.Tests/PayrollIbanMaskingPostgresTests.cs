using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Common;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Pilot sensitive-leak fixes, on real Postgres:
/// (1) an invalid IBAN never reaches <c>payroll_validation_results.message</c> in full, through either
///     persistence path (Process and /validate), and the runbook's read-only detection query finds a
///     legacy full-IBAN row and nothing in a fresh one;
/// (2) the WPS evidence hash-reuse check never searches <c>bank_transfer_files.file_content</c> in SQL.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration")]
public class PayrollIbanMaskingPostgresTests
{
    // Fails mod-97 (the valid check digits for this BBAN are 03, not 04). Distinctive, so a substring hit is real.
    private const string BadIban = "SA0480000000608010167519";

    /// <summary>The runbook's pattern (docs/DEPLOY_ROLLBACK_RUNBOOK.md, "Stored full IBANs"). Keep in sync.</summary>
    private const string RunbookIbanPattern = @"\m[A-Z]{2}[0-9]{2}[A-Z0-9]{11,30}\M";

    private readonly PostgresFixture _fx;
    public PayrollIbanMaskingPostgresTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task InvalidIban_IsPersistedMasked_ByProcessAndByValidate()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, runId) = await SeedRunWithBadIbanAsync(db);
        var ctrl = Controller(db, tenantId);

        (await ctrl.Process(runId, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        await AssertPersistedMaskedAsync(runId);

        db.ChangeTracker.Clear();
        (await ctrl.Validate(runId, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        await AssertPersistedMaskedAsync(runId);
    }

    [Fact]
    public async Task RunbookDetectionQuery_FindsALegacyFullIbanRow_AndIsReadOnly()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, runId) = await SeedRunWithBadIbanAsync(db);
        // The pre-fix message shape, as it sits in production today.
        db.PayrollValidationResults.Add(new PayrollValidationResult
        {
            TenantId = tenantId, PayrollRunId = runId, Severity = "Error", Code = "INVALID_IBAN",
            Message = $"Employee E1 IBAN '{BadIban}' fails country format/length or ISO 13616 mod-97 validation. " +
                      "Correct the IBAN before approving this run.",
        });
        await db.SaveChangesAsync();

        (await CountIbanShapedMessagesAsync(runId)).Should().Be(1, "the runbook query must find what the old code wrote");
        (await CountIbanShapedMessagesAsync(runId)).Should().Be(1, "and it changes nothing");
    }

    [Fact]
    public async Task EvidenceHashReuseCheck_NeverSearchesFileContentInSql_AndStillRejectsReuse()
    {
        Guid tenant, company, batchA, batchB;
        await using (var seed = _fx.CreateDb())
        {
            tenant = await PostgresFixture.SeedMinimalTenant(seed);
            company = await SaudiBankExportTestData.SeedCompanyAsync(seed, tenant);
            batchA = await SaudiBankExportTestData.SeedBatchAsync(seed, tenant, company, 9);
            batchB = await SaudiBankExportTestData.SeedBatchAsync(seed, tenant, company, 10);
        }
        var captured = new List<string>();
        var storage = new MemoryDocumentStorage();
        var bytes = Encoding.UTF8.GetBytes($"bank output {Guid.NewGuid()}");

        async Task<WpsEvidenceUploadResult> Upload(Guid batch)
        {
            await using var db = CapturingDb(captured);
            return await new WpsAcceptanceEvidenceService(db, storage).RecordAsync(
                tenant, company, batch, Guid.NewGuid(), WpsEvidenceKinds.BankOutputFile, "out.txt", "text/plain", bytes, null,
                _ => Task.CompletedTask, default);
        }

        (await Upload(batchA)).Evidence.Should().NotBeNull();
        (await Upload(batchA)).AlreadyRecorded.Should().BeTrue("re-uploading the same file to the same batch is idempotent");
        (await Upload(batchB)).Error.Should().Be("evidence_already_used", "one file cannot prove two months of one legal entity");

        var evidenceReads = captured.Where(c => c.Contains("bank_transfer_files", StringComparison.Ordinal)
                                                && c.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)).ToList();
        evidenceReads.Should().NotBeEmpty("the hash-reuse check reads the evidence rows");
        foreach (var sql in evidenceReads)
        {
            var where = sql[(sql.IndexOf("WHERE", StringComparison.OrdinalIgnoreCase) is var i and >= 0 ? i : sql.Length)..];
            where.Should().NotContain("file_content",
                "file_content holds generated bank files full of IBANs; filter on keys in SQL and compare the SHA in memory");
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────

    private async Task AssertPersistedMaskedAsync(Guid runId)
    {
        await using var check = _fx.CreateDb();
        var rows = await check.PayrollValidationResults.AsNoTracking().Where(r => r.PayrollRunId == runId).ToListAsync();
        rows.Should().ContainSingle(r => r.Code == "INVALID_IBAN")
            .Which.Message.Should().Contain("***7519").And.Contain("wrong checksum");
        rows.Should().NotContain(r => r.Message.Contains(BadIban), "no persisted finding may carry the full IBAN");
        (await CountIbanShapedMessagesAsync(runId)).Should().Be(0);
    }

    private async Task<int> CountIbanShapedMessagesAsync(Guid runId)
    {
        await using var db = _fx.CreateDb();
        await db.Database.OpenConnectionAsync();
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = "SELECT count(*)::int FROM payroll_validation_results WHERE payroll_run_id = @run AND message ~ @pattern";
        var run = cmd.CreateParameter(); run.ParameterName = "run"; run.Value = runId; cmd.Parameters.Add(run);
        var pattern = cmd.CreateParameter(); pattern.ParameterName = "pattern"; pattern.Value = RunbookIbanPattern; cmd.Parameters.Add(pattern);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<(Guid TenantId, Guid RunId)> SeedRunWithBadIbanAsync(ZayraDbContext db)
    {
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var company = new Company
        {
            Id = Guid.NewGuid(), TenantId = tenantId, LegalNameEn = "Iban Mask Co", CountryCode = "SAU",
            Jurisdiction = "KSA-mainland", RegistrationNumber = $"IBM-{Guid.NewGuid():N}", DefaultCurrency = "SAR",
            IsActive = true, CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Companies.Add(company);
        var emp = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = $"IBM-{Guid.NewGuid():N}"[..20],
            FullName = "Iban Mask", Nationality = "Indian", Status = "Active",
            JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = emp.Id, SalaryStructureId = Guid.NewGuid(),
            BasicSalary = 10_000m, HousingAllowance = 3_000m, EffectiveDate = new DateOnly(2024, 1, 1), IsActive = true,
        });
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
        {
            TenantId = tenantId, EmployeeId = emp.Id, Iban = BadIban, MolId = "MOL123456",
            SalaryCurrency = "SAR", BankName = "Test Bank",
        });
        var run = new PayrollRun
        {
            TenantId = tenantId, CompanyId = company.Id, Year = 2026, Month = 9,
            CreatedAtUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        return (tenantId, run.Id);
    }

    private ZayraDbContext CapturingDb(List<string> sink) => new(
        new DbContextOptionsBuilder<ZayraDbContext>()
            .UseNpgsql(_fx.ConnectionString, PostgresFixture.ProductionProviderOptions)
            .AddInterceptors(Zayra.Api.Infrastructure.Jobs.RowLockingInterceptor.Instance)
            .AddInterceptors(Zayra.Api.Infrastructure.Data.AdvisoryXactLockGuardInterceptor.Instance)
            .AddInterceptors(new SqlCapture(sink))
            .Options);

    private sealed class SqlCapture(List<string> sink) : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            lock (sink) sink.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private static StubRuleReader Rules() => new StubRuleReader()
        .Set("gosi.saudi_employee_rate", 0.09m)
        .Set("gosi.saudi_employer_rate", 0.09m)
        .Set("gosi.saned_rate", 0.0075m)
        .Set("gosi.expat_occupational_hazard_rate", 0.02m)
        .Set("gosi.covered_wage_ceiling_sar", 45_000m)
        .Set("ot.standard_multiplier", 1.5m)
        .Set("ot.standard_monthly_hours", 240m)
        .Set("lop.monthly_day_divisor", 30m)
        .Set("lop.standard_work_minutes_per_day", 480m);

    private static PayrollController Controller(ZayraDbContext db, Guid tenantId)
    {
        var rules = Rules();
        var ctrl = new PayrollController(
            db, new DataScopeService(db), new HttpContextAccessor(), new MaskNotifications(),
            new MaskPackResolver(rules), rules, new MaskLetters(), new NullDocumentStorage(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(8));
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", tenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Role, "Admin"),
                    new Claim("permission", "payroll.read"),
                    new Claim("permission", "payroll.write"),
                    new Claim("permission", "employees.read"),
                    new Claim("is_group_scope", "true"),
                }, "Test")),
            },
        };
        return ctrl;
    }
}

file sealed class MaskPackResolver : ICountryPackResolver
{
    private readonly IStatutoryRuleReader _r;
    public MaskPackResolver(IStatutoryRuleReader r) => _r = r;
    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j) =>
        cc is "SAU" or "SA" ? new KsaDeductionCalculator(_r) : new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}

file sealed class MaskNotifications : INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId,
        CancellationToken cancellationToken) => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName,
        Dictionary<string, string> variables, CancellationToken cancellationToken) => Task.CompletedTask;
}

file sealed class MaskLetters : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData data, CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());
}
