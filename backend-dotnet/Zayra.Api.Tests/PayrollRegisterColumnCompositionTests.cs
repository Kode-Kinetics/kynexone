using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Application.Finance;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Domain.Entities;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The salary register's column HEADERS have to describe the columns' real composition, because a payroll
/// officer checks a register by adding it up by hand.
///
/// <para>Two headers did not. "Other" is not "other allowances": <c>PayrollSlip.OtherAllowances</c> is the
/// whole non-basic/housing/transport earnings bucket — other allowances + overtime + bonuses + adjustments
/// + arrears + settlement earnings + configured earnings (<c>PayrollController.Process</c>). And "Loans"
/// sat beside "Deductions" as a second bracketed negative although <c>Deductions</c> already contains the
/// loan/advance EMIs (and the employee GOSI share), so adding the two columns subtracted the loan twice.
/// </para>
///
/// <para>These tests pin the composition the new headers claim, on a real <c>Process</c> run that carries a
/// bonus and a loan — the two things that made the old labels wrong. Nothing here asserts a payroll
/// AMOUNT; they assert only that the columns are disjoint where the register says they are disjoint and
/// nested where it says they are nested. If someone later changes what feeds a column, the label becomes a
/// lie and one of these fails.</para>
/// </summary>
public class PayrollRegisterColumnCompositionTests
{
    private const decimal Basic = 10_000m;
    private const decimal Housing = 2_000m;
    private const decimal Transport = 1_000m;
    private const decimal BonusGross = 2_000m;
    private const decimal LoanEmi = 500m;

    private static (ZayraDbContext db, SqliteConnection conn) CreateSqliteDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new ZayraDbContext(
            new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    private static PayrollController MakeCtrl(ZayraDbContext db, Guid tenantId)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "test-user"),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var httpCtx = new DefaultHttpContext { User = principal };
        var ctrl = new PayrollController(
            db,
            new _RegUnrestrictedScope(),
            new _RegHttpAccessor(httpCtx),
            new _RegNullNotifications(),
            new _RegKsaPackResolver(),
            _RegKsaRules.Rules,
            new _RegNullLetterService(),
            new _RegNullDocStorage(),
            new Zayra.Api.Infrastructure.Documents.PdfRenderGate(4));
        ctrl.ControllerContext = new ControllerContext { HttpContext = httpCtx };
        return ctrl;
    }

    private static async Task<Company> SeedKsaCompany(ZayraDbContext db, Guid tenantId, PayrollRun run)
    {
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = "Test KSA Co",
            CountryCode = "SAU", Jurisdiction = "KSA-mainland", DefaultCurrency = "SAR", IsActive = true,
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        run.CompanyId = company.Id;
        await db.SaveChangesAsync();
        return company;
    }

    /// <summary>One active employee on a KSA package, one approved bonus for the period, one active loan.</summary>
    private static async Task<(Employee emp, PayrollRun run)> SeedRunWithBonusAndLoan(
        ZayraDbContext db, Guid tenantId, int year = 2026, int month = 6)
    {
        var structure = new SalaryStructure
        {
            TenantId = tenantId, Code = "STR-BASE", Name = "Base",
            Currency = "SAR", EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true,
        };
        db.SalaryStructures.Add(structure);

        var emp = new Employee
        {
            TenantId = tenantId, EmployeeCode = "E001", FullName = "Ali Hassan",
            Status = "Active", JoiningDate = new DateTime(2023, 1, 1),
            WorkEmail = "ali@test.com", Nationality = "SAU", ContractType = "Indefinite",
        };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();

        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = emp.Id, SalaryStructureId = structure.Id,
            BasicSalary = Basic, HousingAllowance = Housing, TransportAllowance = Transport,
            EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true,
        });
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
        {
            TenantId = tenantId, EmployeeId = emp.Id,
            Iban = "SA4420000001234567891234", MolId = "MOL-E001", SalaryCurrency = "SAR",
        });

        var run = new PayrollRun
        {
            TenantId = tenantId, Year = year, Month = month, Status = "Draft",
            TotalNetSalary = 0m, TotalGrossSalary = 0m,
        };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();

        var company = await SeedKsaCompany(db, tenantId, run);

        // Bonus — lands inside OtherAllowances, which is exactly why "Other" could not stay "Other".
        var bonusType = new BonusType
        {
            TenantId = tenantId, Code = "PERF", NameEn = "Performance Bonus",
            IsIncludedInGosiBase = false, IsIncludedInWps = true, IsIncludedInEosb = false,
            TaxRegion = "GCC", IsActive = true,
        };
        db.BonusTypes.Add(bonusType);
        await db.SaveChangesAsync();

        var batch = new BonusBatch
        {
            TenantId = tenantId, BatchNumber = "BON-001", BonusTypeId = bonusType.Id,
            BonusTypeName = bonusType.NameEn, PaymentPeriod = $"{year}-{month:D2}",
            Status = "Approved", CreatedBy = null,
        };
        db.BonusBatches.Add(batch);
        await db.SaveChangesAsync();

        db.EmployeeBonuses.Add(new EmployeeBonus
        {
            TenantId = tenantId, BonusBatchId = batch.Id,
            EmployeeId = Guid.NewGuid(), EmployeeIntId = emp.Id,
            EmployeeName = emp.FullName, BonusTypeId = bonusType.Id,
            BonusTypeName = bonusType.NameEn, BasicSalary = Basic,
            CalculationMethod = "Fixed", CalculationValue = BonusGross,
            GrossBonusAmount = BonusGross, TaxWithheld = 0m, BonusAmount = BonusGross,
            PaymentPeriod = $"{year}-{month:D2}", Status = "Approved", TaxRegion = "GCC",
        });

        // Loan — its EMI lands inside Deductions, which is why "Loans" cannot sit beside it as a peer.
        db.EmployeeLoans.Add(new EmployeeLoan
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeId = Guid.NewGuid(), EmployeeIntId = emp.Id,
            EmployeeName = emp.FullName, LoanTypeId = Guid.NewGuid(), LoanTypeName = "General",
            LoanNumber = "LN-1", RequestedAmount = 6_000m, ApprovedAmount = 6_000m,
            RequestedInstallments = 12, ApprovedInstallments = 12, InstallmentAmount = LoanEmi,
            OutstandingBalance = 6_000m, Status = "Active",
        });
        await db.SaveChangesAsync();

        return (emp, run);
    }

    // ── The register's arithmetic ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Basic + Housing + Transport + "Other earnings" = Gross, and Gross − Deductions = Net. These four
    /// earning columns are what the register prints side by side; if they stop being disjoint, a reader
    /// adding the row gets a different total from the one printed beside it.
    /// </summary>
    [Fact]
    public async Task RegisterRow_DisjointColumns_SumToTheRowTotal()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn;
        await using var __ = db;

        var tenantId = Guid.NewGuid();
        var (_, run) = await SeedRunWithBonusAndLoan(db, tenantId);
        var ctrl = MakeCtrl(db, tenantId);

        (await ctrl.Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        var register = ((OkObjectResult)await ctrl.Register(run.Id, CancellationToken.None))
            .Value.Should().BeAssignableTo<List<PayrollSlipDto>>().Subject;
        register.Should().HaveCount(1);
        var row = register[0];

        (row.BasicSalary + row.HousingAllowance + row.TransportAllowance + row.OtherAllowances)
            .Should().Be(row.GrossSalary,
                "the register prints Basic, Housing, Transport and Other earnings as four disjoint columns beside Gross");

        (row.GrossSalary - row.Deductions).Should().Be(row.NetSalary,
            "the register prints Net as the one figure left after Deductions");
    }

    /// <summary>
    /// "Other earnings" carries the bonus. That is the whole reason the column cannot be headed "Other
    /// allowances": the structure's own other-allowance here is zero, and the column is still 2,000.
    /// </summary>
    [Fact]
    public async Task OtherEarningsColumn_CarriesMoreThanAllowances()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn;
        await using var __ = db;

        var tenantId = Guid.NewGuid();
        var (_, run) = await SeedRunWithBonusAndLoan(db, tenantId);
        var ctrl = MakeCtrl(db, tenantId);

        (await ctrl.Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        var row = ((List<PayrollSlipDto>)((OkObjectResult)await ctrl.Register(run.Id, CancellationToken.None)).Value!)[0];

        // The salary structure declares no other allowance at all, so everything in this column is
        // non-allowance earnings — here, the approved bonus.
        row.OtherAllowances.Should().Be(BonusGross,
            "OtherAllowances is the whole non-basic/housing/transport earnings bucket, not the structure's other-allowance column");

        // ...and it is inside Gross exactly once. A register that ALSO printed an Overtime or Bonus column
        // beside this one would be double-printing the same riyals.
        row.GrossSalary.Should().Be(Basic + Housing + Transport + BonusGross);
    }

    /// <summary>
    /// "of which: Loans" and "of which GOSI" are slices of Deductions, never peers of it. Adding either to
    /// Deductions — which the old side-by-side bracketed columns invited — subtracts the same money twice.
    /// </summary>
    [Fact]
    public async Task LoansAndGosi_AreInsideDeductions_NotBesideThem()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn;
        await using var __ = db;

        var tenantId = Guid.NewGuid();
        var (_, run) = await SeedRunWithBonusAndLoan(db, tenantId);
        var ctrl = MakeCtrl(db, tenantId);

        (await ctrl.Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        var row = ((List<PayrollSlipDto>)((OkObjectResult)await ctrl.Register(run.Id, CancellationToken.None)).Value!)[0];

        row.LoanDeductions.Should().Be(LoanEmi, "the seeded loan's instalment is withheld this period");
        row.EmployeeStatutoryTotal.Should().BeGreaterThan(0m, "a Saudi employee on a KSA pack pays GOSI");

        // Both are components OF Deductions.
        row.LoanDeductions.Should().BeLessThanOrEqualTo(row.Deductions!.Value);
        row.EmployeeStatutoryTotal.Should().BeLessThanOrEqualTo(row.Deductions!.Value);
        (row.LoanDeductions + row.EmployeeStatutoryTotal).Should().BeLessThanOrEqualTo(row.Deductions!.Value,
            "loans and GOSI are disjoint slices of one Deductions total, so together they still fit inside it");

        // And the net proves it: subtracting the loan a second time would not reconcile.
        (row.GrossSalary - row.Deductions).Should().Be(row.NetSalary);
        (row.GrossSalary - row.Deductions - row.LoanDeductions).Should().NotBe(row.NetSalary,
            "if this ever held, the loan would genuinely be outside Deductions and the label would need to change back");
    }

    // ── The zero-employee refusal ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A tenant that has not added anybody yet hits Process on its first day. Refusing is right; refusing
    /// with a raw code is not. The body must carry a sentence a payroll officer can act on.
    /// </summary>
    [Fact]
    public async Task ProcessingARunWithNobodyToPay_Refuses_WithASentenceNotACode()
    {
        var (db, conn) = CreateSqliteDb();
        await using var _ = conn;
        await using var __ = db;

        var tenantId = Guid.NewGuid();
        var run = new PayrollRun
        {
            TenantId = tenantId, Year = 2026, Month = 6, Status = "Draft",
            TotalNetSalary = 0m, TotalGrossSalary = 0m,
        };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        await SeedKsaCompany(db, tenantId, run);

        var result = await MakeCtrl(db, tenantId).Process(run.Id, CancellationToken.None);

        var body = result.Should().BeOfType<UnprocessableEntityObjectResult>().Subject.Value!;
        var error = (string)body.GetType().GetProperty("error")!.GetValue(body)!;
        var message = (string)body.GetType().GetProperty("message")!.GetValue(body)!;

        error.Should().Be("no_company_employees", "the machine-readable code is a stable contract");

        message.Should().NotContain("_", "the message is prose for a human, never the error code");
        message.Should().Contain("no active employees to pay");
        message.Should().Contain("Test KSA Co", "it names the company the operator chose");
        message.Should().Contain("June 2026", "it names the period, so the operator knows which run refused");
        message.Should().Contain("process the run again", "it states the next action");
    }
}

/// <summary>KSA rates matching the seeder defaults, so Process computes real GOSI and OT figures.</summary>
file static class _RegKsaRules
{
    internal static readonly StubRuleReader Rules = new StubRuleReader()
        .Set("gosi.saudi_employee_rate",            0.09m)
        .Set("gosi.saudi_employer_rate",            0.09m)
        .Set("gosi.saned_rate",                     0.0075m)
        .Set("gosi.expat_occupational_hazard_rate", 0.02m)
        .Set("gosi.covered_wage_ceiling_sar",       45_000m)
        .Set("ot.standard_multiplier",              1.5m)
        .Set("ot.standard_monthly_hours",           240m)
        .Set("lop.monthly_day_divisor",             30m)
        .Set("lop.standard_work_minutes_per_day",   480m);
}

file sealed class _RegUnrestrictedScope : Zayra.Api.Application.Common.IDataScopeService
{
    public Task<Zayra.Api.Application.Common.DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Application.Common.DataScope { Level = Zayra.Api.Application.Common.DataScopeLevel.Organization, AllowedEmployeeIds = null });
}

file sealed class _RegHttpAccessor : IHttpContextAccessor
{
    public _RegHttpAccessor(HttpContext ctx) => HttpContext = ctx;
    public HttpContext? HttpContext { get; set; }
}

file sealed class _RegNullNotifications : Zayra.Api.Infrastructure.Notifications.INotificationService
{
    public Task NotifyAsync(Guid tenantId, Guid? userId, string title, string message, string entityName, string? entityId, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid tenantId, string templateCode, string toAddress, string toName, Dictionary<string, string> variables, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Real KSA calculators, so the GOSI slice of Deductions is a real non-zero figure.</summary>
file sealed class _RegKsaPackResolver : ICountryPackResolver
{
    private static readonly KsaDeductionCalculator _calc = new(_RegKsaRules.Rules);

    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j)
        => cc == "SAU" ? _calc : new DefaultStatutoryDeductionCalculator();
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new DefaultEndOfServiceCalculator();
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}

file sealed class _RegNullLetterService : Zayra.Api.Infrastructure.Documents.Letters.ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(Zayra.Api.Infrastructure.Documents.Letters.PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(Zayra.Api.Infrastructure.Documents.Letters.OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}

file sealed class _RegNullDocStorage : Zayra.Api.Infrastructure.Documents.IDocumentStorage
{
    public Task<Zayra.Api.Infrastructure.Documents.StoredDocument> SaveAsync(Guid tenantId, IFormFile file, CancellationToken ct)
        => Task.FromResult(new Zayra.Api.Infrastructure.Documents.StoredDocument(file.FileName, file.ContentType, "storage/test", "/tmp/test"));
    public Task<byte[]> GetBytesAsync(Guid tenantId, string storageUrl, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public string ResolvePath(string storageUrl) => "/tmp/test";
}
