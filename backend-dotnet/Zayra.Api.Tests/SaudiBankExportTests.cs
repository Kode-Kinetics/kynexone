using System.IO.Compression;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Payroll.SaudiBankExports;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>Seed data shared by the unit and Postgres integration tests. Test-only facts: every ID,
/// account and address here is synthetic.</summary>
internal static class SaudiBankExportTestData
{
    public static readonly string IbanA = IbanValidator.WithValidCheckDigits("SA0080000000608010167519");
    public const string AnbInternal = "0108057386290038";
    /// <summary>An ANB (bank code 30) Saudi IBAN. The KSA wage-file rules require a 24-character SA IBAN
    /// for every line, so the service fixture pays E2 by IBAN; the pure ANB generator tests still cover
    /// the 16-digit internal-account form ANB's own layout allows.</summary>
    public static readonly string IbanAnb = IbanValidator.WithValidCheckDigits("SA0030100000608010167519");

    public sealed class FakeAddresses : ISaudiBankEmployeeAddressSource
    {
        public Task<IReadOnlyDictionary<int, (string Line1, string Line2, string Line3)>> LoadAsync(
            Guid tenantId, IReadOnlyCollection<int> employeeIds, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<int, (string, string, string)>>(
                employeeIds.ToDictionary(id => id, _ => ("Riyadh", "Olaya", "KSA")));
    }

    public static SaudiBankExportSettingsDto Settings() => new()
    {
        FormatId = SaudiBankExportFormats.AnbConnectCsvV1, MolEstablishmentId = "7001234567",
        MainAccountNumber = "0108061198800026", OrganizationName = "Test Establishment Co",
        OrganizationAddress1 = "Riyadh", OrganizationAddress2 = "Olaya", OrganizationAddress3 = "KSA",
        CompanyName = "Test Establishment", Narrative = "Payroll Sep 2026", BatchType = "PAYROLL",
    };

    public static SaudiBankExportBatchRequest Request(string reference = "2026092601") => new()
    {
        BatchReference = reference,
        PaymentDate = DateTime.UtcNow.AddDays(5).ToString("yyyy-MM-dd"),
    };

    public static async Task<Guid> SeedCompanyAsync(ZayraDbContext db, Guid tenantId)
    {
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = $"Test Establishment {Guid.NewGuid():N}"[..30],
            CountryCode = "SA", DefaultCurrency = "SAR",
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        return company.Id;
    }

    /// <summary>A Locked run with two reconciled slips and a Pending SAR batch covering both.</summary>
    public static async Task<Guid> SeedBatchAsync(ZayraDbContext db, Guid tenantId, Guid companyId, int month)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var e1 = new Employee
        {
            TenantId = tenantId, CompanyId = companyId, EmployeeCode = $"E1-{tag}", FullName = "Omar Test",
            EnglishName = "Omar Test", Status = "Active", ReadinessState = "Ready", JoiningDate = DateTime.UtcNow,
            IdType = "NationalId", IdNumber = "1012345678", Nationality = "Saudi",
        };
        var e2 = new Employee
        {
            TenantId = tenantId, CompanyId = companyId, EmployeeCode = $"E2-{tag}", FullName = "Sara Test",
            EnglishName = "Sara Test", Status = "Active", ReadinessState = "Ready", JoiningDate = DateTime.UtcNow,
            IqamaNumber = "2012345678", Nationality = "Egyptian",
        };
        // Explicit pre-approved BIC fixtures; addresses are intentionally absent until supplied.
        e1.WpsBankDetails = JsonSerializer.Serialize(new { schema = ApprovedSaudiBeneficiaryDetails.Schema, bicCode = "RJHISARI" });
        e2.WpsBankDetails = JsonSerializer.Serialize(new { schema = ApprovedSaudiBeneficiaryDetails.Schema, bicCode = "ARNBSARI" });
        db.Employees.AddRange(e1, e2);
        await db.SaveChangesAsync();

        var run = new PayrollRun
        {
            TenantId = tenantId, CompanyId = companyId, Year = 2026, Month = month, Status = "Locked",
            TotalNetSalary = 11550.50m, EmployeeCount = 2,
        };
        db.PayrollRuns.Add(run);
        db.PayrollSlips.AddRange(
            new PayrollSlip
            {
                TenantId = tenantId, CompanyId = companyId, RunId = run.Id, EmployeeId = e1.Id, EmployeeCode = e1.EmployeeCode,
                BasicSalary = 5000m, HousingAllowance = 1250m, TransportAllowance = 500m, OtherAllowances = 0m,
                GrossSalary = 6750m, Deductions = 450m, NetSalary = 6300m, Status = "Locked",
            },
            new PayrollSlip
            {
                TenantId = tenantId, CompanyId = companyId, RunId = run.Id, EmployeeId = e2.Id, EmployeeCode = e2.EmployeeCode,
                BasicSalary = 4000.50m, HousingAllowance = 1000m, TransportAllowance = 0m, OtherAllowances = 250m,
                GrossSalary = 5250.50m, Deductions = 0m, NetSalary = 5250.50m, Status = "Locked",
            });
        db.EmployeePayrollProfiles.AddRange(
            new EmployeePayrollProfile { TenantId = tenantId, EmployeeId = e1.Id, Iban = IbanA, SalaryCurrency = "SAR", BankRoutingCode = "RJHISARI" },
            new EmployeePayrollProfile { TenantId = tenantId, EmployeeId = e2.Id, Iban = IbanAnb, SalaryCurrency = "SAR", BankRoutingCode = "ARNBSARI" });
        var batch = new PayrollPaymentBatch
        {
            TenantId = tenantId, PayrollRunId = run.Id, BatchNumber = $"PB-{tag}", PaymentMethod = "WPS",
            TotalAmount = 11550.50m, Currency = "SAR", Status = "Pending", WpsStatus = WpsStatuses.Draft,
        };
        db.PayrollPaymentBatches.Add(batch);
        db.PayrollPaymentRecords.AddRange(
            new PayrollPaymentRecord { TenantId = tenantId, PaymentBatchId = batch.Id, EmployeeId = e1.Id, Amount = 6300m, Iban = IbanA, Status = "Pending" },
            new PayrollPaymentRecord { TenantId = tenantId, PaymentBatchId = batch.Id, EmployeeId = e2.Id, Amount = 5250.50m, Iban = IbanAnb, Status = "Pending" });
        await db.SaveChangesAsync();
        return batch.Id;
    }
}

public class SaudiBankExportTests
{
    // ── Pure generator ────────────────────────────────────────────────────────────────────────

    private static AnbHeaderInput Header(string batch = "2026092601") => new(
        batch, "PAYROLL", "7001234567", "0108061198800026", new DateOnly(2026, 10, 1),
        "Test Establishment Co", "Riyadh", "Olaya", "KSA", "Payroll Sep 2026", "Test Establishment");

    private static AnbPaymentInput Row1() => new(1, "E1", "1012345678", SaudiBankExportTestData.IbanA,
        6300m, 5000m, 1250m, 500m, 450m, "RJHISARI", "Omar Test", "Riyadh", "Olaya", "KSA");

    private static AnbPaymentInput Row2() => new(2, "E2", "2012345678", SaudiBankExportTestData.AnbInternal,
        5250.50m, 4000.50m, 1000m, 250m, 0m, "ARNBSARI", "Sara Test", "Jeddah", "Rawdah", "KSA");

    [Fact]
    public void Generator_EmitsExactColumnOrder_CrLf_NoBom_LosslessAmounts()
    {
        // Rows passed out of order on purpose: output order is stable by employee ref.
        var result = AnbConnectCsvGenerator.Generate(Header(), new[] { Row2(), Row1() }, 11550.50m);

        result.Ok.Should().BeTrue(string.Join("; ", result.Errors.Select(e => e.Code)));
        var header = result.Files!.Single(f => f.Name == "header.csv").Content;
        var body = result.Files!.Single(f => f.Name == "body.csv").Content;
        header.Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, "UTF-8 without BOM");

        Encoding.UTF8.GetString(header).Should().Be(
            "batchNumber,batchType,molEstablishmentId,mainAccountNumber,creditValueDate,organizationName,organizationAddress1,organizationAddress2,organizationAddress3,paymentCount,totalPayrollAmount,narrative,companyName\r\n"
            + "2026092601,PAYROLL,7001234567,0108061198800026,261001,Test Establishment Co,Riyadh,Olaya,KSA,2,11550.5,Payroll Sep 2026,Test Establishment\r\n");
        Encoding.UTF8.GetString(body).Should().Be(
            "employeeId,employeeAccountNumber,salaryAmount,basicSalary,housingAllowance,otherEarnings,salaryDeductions,bicCode,employeeName,employeeAddress1,employeeAddress2,employeeAddress3\r\n"
            + $"1012345678,{SaudiBankExportTestData.IbanA},6300,5000,1250,500,450,RJHISARI,Omar Test,Riyadh,Olaya,KSA\r\n"
            + "2012345678,0108057386290038,5250.5,4000.5,1000,250,0,ARNBSARI,Sara Test,Jeddah,Rawdah,KSA\r\n");
    }

    [Fact]
    public void Generator_IsDeterministic_SameInputSameHashes()
    {
        var a = AnbConnectCsvGenerator.Generate(Header(), new[] { Row1(), Row2() }, 11550.50m);
        var b = AnbConnectCsvGenerator.Generate(Header(), new[] { Row2(), Row1() }, 11550.50m);
        a.Files!.Select(f => f.Sha256).Should().Equal(b.Files!.Select(f => f.Sha256));
    }

    public static IEnumerable<object[]> BadRows()
    {
        yield return new object[] { Row1() with { EmployeeNationalId = "802630" }, "employee_id_invalid" };
        yield return new object[] { Row1() with { EmployeeName = "=HYPERLINK(1)" }, "field_formula_prefix" };
        yield return new object[] { Row1() with { Address1 = "Riyadh\tNorth" }, "field_control_character" };
        yield return new object[] { Row1() with { Address2 = " Olaya" }, "field_surrounding_whitespace" };
        yield return new object[] { Row1() with { Address3 = new string('A', 31) }, "field_length" };
        yield return new object[] { Row1() with { SalaryAmount = 6300.005m, SalaryDeductions = 449.995m }, "amount_precision" };
        yield return new object[] { Row1() with { SalaryAmount = 6300.01m }, "net_unreconciled" };
        yield return new object[] { Row1() with { SalaryDeductions = -1m, SalaryAmount = 6751m }, "amount_negative" };
        yield return new object[] { Row1() with { AccountNumber = "0108057386290045" }, "account_internal_not_anb" };
        yield return new object[] { Row1() with { AccountNumber = "SA0380000000608010167518" }, "account_iban_invalid" };
        yield return new object[] { Row1() with { AccountNumber = SaudiBankExportTestData.IbanA.ToLowerInvariant() }, "account_invalid" };
        yield return new object[] { Row1() with { BicCode = "RJHIAERI" }, "bic_invalid" };
        yield return new object[] { Row1() with { BicCode = null }, "bic_missing" };
        yield return new object[] { Row1() with { Address1 = null, Address2 = null, Address3 = null }, "employee_address_missing" };
    }

    [Theory]
    [MemberData(nameof(BadRows))]
    public void Generator_FailsClosed_OnInvalidRow_AndEmitsNoFiles(AnbPaymentInput bad, string expectedCode)
    {
        var result = AnbConnectCsvGenerator.Generate(Header(), new[] { bad, Row2() }, bad.SalaryAmount + 5250.50m);
        result.Files.Should().BeNull();
        result.Errors.Should().Contain(e => e.Code == expectedCode && e.EmployeeId == 1);
        result.Errors.Should().NotContain(e => e.Message.Contains("1012345678"), "national IDs never appear in messages");
    }

    [Fact]
    public void Generator_Blocks_DuplicateIdsAccounts_AndTotalMismatch()
    {
        var dup = Row2() with { EmployeeNationalId = "1012345678", AccountNumber = SaudiBankExportTestData.IbanA, BicCode = "RJHISARI" };
        var result = AnbConnectCsvGenerator.Generate(Header(), new[] { Row1(), dup }, 1m);
        result.Errors.Select(e => e.Code).Should().Contain(new[] { "duplicate_employee_id", "duplicate_account", "total_mismatch" });
    }

    [Theory]
    [InlineData("")]
    [InlineData("12AB")]
    [InlineData("123456789012345678901")]
    public void Generator_Rejects_BadBatchNumber(string batch) =>
        AnbConnectCsvGenerator.Generate(Header(batch), new[] { Row1() }, 6300m).Errors
            .Should().Contain(e => e.Code == "batch_reference_invalid");

    [Fact]
    public void Generator_Rejects_IbanAsMainAccount_AndUnknownBatchType()
    {
        var h = Header() with { MainAccountNumber = SaudiBankExportTestData.IbanA, BatchType = "payroll" };
        var codes = AnbConnectCsvGenerator.Generate(h, new[] { Row1() }, 6300m).Errors.Select(e => e.Code);
        codes.Should().Contain(new[] { "main_account_invalid", "batch_type_invalid" });
    }

    [Fact]
    public void Formats_OnlyAnbConnect_AndUnknownIdsFailClosed()
    {
        SaudiBankExportFormats.Supported.Should().ContainSingle(f => f.Id == "anb-connect-csv-v1"
            && f.AcceptanceStatus == "specification-implemented; bank-acceptance-not-verified"
            && f.SourceUrl == "https://connect.anb.com.sa/apis/api/payroll-payment");
        SaudiBankExportFormats.Find("ANB-CONNECT-CSV-V1").Should().BeNull();
        SaudiBankExportFormats.Find("generic-xml").Should().BeNull();
        SaudiBankExportService.ValidateSettings(new SaudiBankExportSettingsDto { FormatId = "sab-wps-v1" })
            .Should().Contain(e => e.Code == "format_unsupported");
    }

    // ── Service (EF InMemory) ─────────────────────────────────────────────────────────────────

    private static ZayraDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ZayraDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<(ZayraDbContext Db, Guid Tenant, Guid Company, Guid Batch, SaudiBankExportService Svc)> Arrange(
        ISaudiBankEmployeeAddressSource? addresses = null)
    {
        var db = NewDb();
        var tenant = Guid.NewGuid();
        var company = await SaudiBankExportTestData.SeedCompanyAsync(db, tenant);
        var batch = await SaudiBankExportTestData.SeedBatchAsync(db, tenant, company, 9);
        var svc = new SaudiBankExportService(db, addresses ?? new SaudiBankExportTestData.FakeAddresses());
        (await svc.SaveSettingsAsync(tenant, company, Guid.NewGuid(), SaudiBankExportTestData.Settings(), default))
            .Outcome.Should().Be(SaudiBankExportOutcome.Ok);
        return (db, tenant, company, batch, svc);
    }

    [Fact]
    public async Task Validate_ReconciledBatch_CanExport_AndWritesNothing()
    {
        var (db, tenant, _, batch, svc) = await Arrange();
        var before = db.BankTransferFiles.Count() + db.PayrollAuditLogs.Count();

        var v = (await svc.ValidateAsync(tenant, batch, SaudiBankExportTestData.Request(), default)).Value!;

        v.CanExport.Should().BeTrue(string.Join("; ", v.Errors.Select(e => e.Code)));
        v.EmployeeCount.Should().Be(2);
        v.TotalAmount.Should().Be(11550.50m);
        v.Currency.Should().Be("SAR");
        (db.BankTransferFiles.Count() + db.PayrollAuditLogs.Count()).Should().Be(before);
    }

    [Fact]
    public async Task Validate_WithoutAddressSource_BlocksEveryRow_NeverDefaults()
    {
        var (_, tenant, _, batch, svc) = await Arrange(new NoEmployeeAddressSource());
        var v = (await svc.ValidateAsync(tenant, batch, SaudiBankExportTestData.Request(), default)).Value!;
        v.CanExport.Should().BeFalse();
        v.Errors.Count(e => e.Code == "employee_address_missing").Should().Be(2);
    }

    [Fact]
    public async Task Generate_IsIdempotent_Immutable_AndTouchesNoPaymentState()
    {
        var (db, tenant, _, batch, svc) = await Arrange();
        var req = SaudiBankExportTestData.Request();

        var first = await svc.GenerateAsync(tenant, Guid.NewGuid(), batch, req, default);
        var second = await svc.GenerateAsync(tenant, Guid.NewGuid(), batch, req, default);

        first.Outcome.Should().Be(SaudiBankExportOutcome.Ok);
        second.Outcome.Should().Be(SaudiBankExportOutcome.Ok);
        second.Value!.Id.Should().Be(first.Value!.Id);
        second.Value.Files.Should().BeEquivalentTo(first.Value.Files);
        first.Value.DownloadUrl.Should().Be($"/api/payroll/bank-exports/batches/{batch:D}/download");
        db.BankTransferFiles.Count(f => f.PaymentBatchId == batch).Should().Be(1);
        db.PayrollAuditLogs.Count(a => a.Action == "payroll.bank_export.generated").Should().Be(1);
        db.PayrollAuditLogs.Where(a => a.Action.StartsWith("payroll.bank_export")).AsEnumerable()
            .Should().OnlyContain(a => !a.MetadataJson.Contains("1012345678") && !a.MetadataJson.Contains(SaudiBankExportTestData.IbanA),
                "audit carries ids/hashes/counts only");

        var b = db.PayrollPaymentBatches.AsNoTracking().Single(x => x.Id == batch);
        b.WpsStatus.Should().Be(WpsStatuses.Draft);
        b.Status.Should().Be("Pending");
        db.PayrollPaymentRecords.AsNoTracking().Where(r => r.PaymentBatchId == batch).Should().OnlyContain(r => r.Status == "Pending");
        db.FinanceGlEntries.Count().Should().Be(0);
    }

    [Fact]
    public async Task Generate_ChangedInputsAfterExport_Is409_AndArtifactIsNotReplaced()
    {
        var (db, tenant, _, batch, svc) = await Arrange();
        var req = SaudiBankExportTestData.Request();
        var first = await svc.GenerateAsync(tenant, Guid.NewGuid(), batch, req, default);
        var stored = db.BankTransferFiles.AsNoTracking().Single(f => f.PaymentBatchId == batch).FileContent;

        var changedSettings = SaudiBankExportTestData.Settings();
        changedSettings.Narrative = "Payroll Sep 2026 v2";
        await svc.SaveSettingsAsync(tenant, db.Companies.Single().Id, Guid.NewGuid(), changedSettings, default);
        var again = await svc.GenerateAsync(tenant, Guid.NewGuid(), batch, req, default);
        var otherRef = await svc.GenerateAsync(tenant, Guid.NewGuid(), batch, SaudiBankExportTestData.Request("99"), default);

        first.Outcome.Should().Be(SaudiBankExportOutcome.Ok);
        again.Outcome.Should().Be(SaudiBankExportOutcome.Conflict);
        again.Error.Should().Be("export_inputs_changed");
        otherRef.Error.Should().Be("batch_already_exported");
        db.BankTransferFiles.AsNoTracking().Single(f => f.PaymentBatchId == batch).FileContent.Should().Be(stored);
    }

    [Fact]
    public async Task Generate_BlocksReferenceReuse_AcrossBatchesOfSameCompany()
    {
        var (db, tenant, company, batch, svc) = await Arrange();
        var batch2 = await SaudiBankExportTestData.SeedBatchAsync(db, tenant, company, 10);
        (await svc.GenerateAsync(tenant, Guid.NewGuid(), batch, SaudiBankExportTestData.Request("555"), default))
            .Outcome.Should().Be(SaudiBankExportOutcome.Ok);

        var second = await svc.GenerateAsync(tenant, Guid.NewGuid(), batch2, SaudiBankExportTestData.Request("555"), default);

        second.Outcome.Should().Be(SaudiBankExportOutcome.Invalid);
        second.Validation!.Errors.Should().Contain(e => e.Code == "batch_reference_in_use");
    }

    [Fact]
    public async Task Generate_RefusesNonLockedRun_UnresolvedErrors_AndSlipDrift()
    {
        var (db, tenant, _, batch, svc) = await Arrange();
        var runId = db.PayrollPaymentBatches.Single(b => b.Id == batch).PayrollRunId;
        var run = db.PayrollRuns.Single(r => r.Id == runId);
        run.Status = "Approved";
        db.PayrollValidationResults.Add(new PayrollValidationResult { TenantId = tenant, PayrollRunId = runId, Severity = "Error", Code = "X" });
        db.PayrollSlips.First(s => s.RunId == runId).NetSalary += 1m;
        await db.SaveChangesAsync();

        var r = await svc.GenerateAsync(tenant, Guid.NewGuid(), batch, SaudiBankExportTestData.Request(), default);

        r.Outcome.Should().Be(SaudiBankExportOutcome.Invalid);
        r.Validation!.Errors.Select(e => e.Code).Should().Contain(new[] { "run_not_locked", "payroll_errors_unresolved", "slip_net_unreconciled" });
        db.BankTransferFiles.Count().Should().Be(0);
    }

    [Theory]
    [InlineData(WpsStatuses.Paid)]
    [InlineData(WpsStatuses.Accepted)]
    [InlineData(WpsStatuses.Voided)]
    public async Task Generate_RefusesIneligibleBatch(string wpsStatus)
    {
        var (db, tenant, _, batch, svc) = await Arrange();
        db.PayrollPaymentBatches.Single(b => b.Id == batch).WpsStatus = wpsStatus;
        await db.SaveChangesAsync();
        var r = await svc.GenerateAsync(tenant, Guid.NewGuid(), batch, SaudiBankExportTestData.Request(), default);
        r.Validation!.Errors.Should().Contain(e => e.Code == "batch_ineligible");
    }

    [Fact]
    public async Task Download_ReturnsExactPersistedBytes_AndIsRefusedAfterVoid()
    {
        var (db, tenant, _, batch, svc) = await Arrange();
        var gen = (await svc.GenerateAsync(tenant, Guid.NewGuid(), batch, SaudiBankExportTestData.Request(), default)).Value!;

        var dl = await svc.DownloadAsync(tenant, Guid.NewGuid(), batch, default);

        dl.Outcome.Should().Be(SaudiBankExportOutcome.Ok);
        dl.Value!.FileName.Should().Be("anb-connect-batch-2026092601.zip");
        using var zip = new ZipArchive(new MemoryStream(dl.Value.ZipBytes));
        zip.Entries.Select(e => e.FullName).Should().Equal("header.csv", "body.csv");
        foreach (var entry in zip.Entries)
        {
            using var ms = new MemoryStream();
            entry.Open().CopyTo(ms);
            AnbConnectCsvGenerator.Sha256Hex(ms.ToArray()).Should().Be(gen.Files.Single(f => f.Name == entry.FullName).Sha256);
        }

        var runId = db.PayrollPaymentBatches.Single(b => b.Id == batch).PayrollRunId;
        db.PayrollRuns.Single(r => r.Id == runId).Status = "Voided";
        await db.SaveChangesAsync();
        (await svc.DownloadAsync(tenant, Guid.NewGuid(), batch, default)).Error.Should().Be("run_voided");
    }

    [Fact]
    public async Task Download_DetectsTamperedArtifact()
    {
        var (db, tenant, _, batch, svc) = await Arrange();
        await svc.GenerateAsync(tenant, Guid.NewGuid(), batch, SaudiBankExportTestData.Request(), default);
        var row = db.BankTransferFiles.Single(f => f.PaymentBatchId == batch);
        var node = System.Text.Json.Nodes.JsonNode.Parse(row.FileContent)!;
        node["files"]![1]!["contentBase64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("tampered"));
        row.FileContent = node.ToJsonString();
        await db.SaveChangesAsync();

        (await svc.DownloadAsync(tenant, Guid.NewGuid(), batch, default)).Error.Should().Be("artifact_integrity_failed");
    }

    // ── Controller authorization ──────────────────────────────────────────────────────────────

    /// <summary>Activation config enabling exactly one tenant/company pair. Shared with
    /// <see cref="SaudiBankExportActivationTests"/> so both files agree on what "enabled" means.</summary>
    internal static IConfiguration EnabledActivationConfig(Guid tenantId, Guid companyId) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{SaudiBankExportActivation.Section}:Enabled"] = "true",
            [$"{SaudiBankExportActivation.Section}:Companies:0:TenantId"] = tenantId.ToString(),
            [$"{SaudiBankExportActivation.Section}:Companies:0:CompanyId"] = companyId.ToString(),
        }).Build();

    /// <summary>Test infrastructure note: every OTHER test in this file below exercises the
    /// controller through activation that is explicitly ENABLED for (tenant, scopedCompany) — the
    /// default-OFF behaviour itself is covered exhaustively in <see cref="SaudiBankExportActivationTests"/>.
    /// Cross-company/cross-tenant assertions here still hold because those checks (entity scope, batch
    /// tenant ownership) run and refuse BEFORE the activation check is ever reached.</summary>
    internal static SaudiBankExportsController Ctrl(
        ZayraDbContext db, Guid tenant, Guid? scopedCompany, IConfiguration? activationConfiguration, params string[] permissions)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenant.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        };
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));
        claims.Add(scopedCompany is Guid c
            ? new Claim(EntityScopeContext.V2ClaimType, JsonSerializer.Serialize(new { v = 2, m = "companies", c = new[] { c.ToString() } }))
            : new Claim("is_group_scope", "true"));
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        return new SaudiBankExportsController(db, new SaudiBankExportTestData.FakeAddresses(), activationConfiguration)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    internal static SaudiBankExportsController Ctrl(ZayraDbContext db, Guid tenant, Guid? scopedCompany, params string[] permissions) =>
        Ctrl(db, tenant, scopedCompany, scopedCompany is Guid c ? EnabledActivationConfig(tenant, c) : null, permissions);

    [Fact]
    public async Task Controller_CrossCompanyBatch_IsForbidden_AndNothingIsWritten()
    {
        var (db, tenant, _, batch, _) = await Arrange();
        var ctrl = Ctrl(db, tenant, Guid.NewGuid(), "payroll.export");

        (await ctrl.Generate(batch, SaudiBankExportTestData.Request(), default)).Should().BeOfType<ForbidResult>();
        (await ctrl.Context(batch, default)).Should().BeOfType<ForbidResult>();
        (await ctrl.Download(batch, default)).Should().BeOfType<ForbidResult>();
        db.BankTransferFiles.Count().Should().Be(0);
    }

    [Fact]
    public async Task Controller_OtherTenantBatch_IsNotFound()
    {
        var (db, _, company, batch, _) = await Arrange();
        (await Ctrl(db, Guid.NewGuid(), company, "payroll.export").Validate(batch, SaudiBankExportTestData.Request(), default))
            .Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Controller_SettingsMutation_RequiresStructureManage_AndCompanyScope()
    {
        var (db, tenant, company, _, _) = await Arrange();

        (await Ctrl(db, tenant, company, "payroll.export").PutSettings(company, SaudiBankExportTestData.Settings(), default))
            .Should().BeOfType<ForbidResult>();
        (await Ctrl(db, tenant, Guid.NewGuid(), "payroll.export", "payroll.structure_manage")
            .PutSettings(company, SaudiBankExportTestData.Settings(), default)).Should().BeOfType<ForbidResult>();
        (await Ctrl(db, tenant, Guid.NewGuid(), "payroll.export").GetSettings(company, default)).Should().BeOfType<ForbidResult>();
        (await Ctrl(db, tenant, company, "payroll.export", "payroll.structure_manage")
            .PutSettings(company, SaudiBankExportTestData.Settings(), default)).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Controller_MissingExportPermission_IsForbidden()
    {
        var (db, tenant, company, batch, _) = await Arrange();
        var ctrl = Ctrl(db, tenant, company);
        ctrl.Formats().Should().BeOfType<ForbidResult>();
        (await ctrl.Validate(batch, SaudiBankExportTestData.Request(), default)).Should().BeOfType<ForbidResult>();
    }
}
