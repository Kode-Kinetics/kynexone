using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Zayra.Api.Application.Common;
using Zayra.Api.Application.CountryPack;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Compliance;
using Zayra.Api.Infrastructure.CountryPack;
using Zayra.Api.Infrastructure.CountryPack.Ksa;
using Zayra.Api.Infrastructure.Documents;
using Zayra.Api.Infrastructure.Documents.Letters;
using Zayra.Api.Infrastructure.Notifications;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// Pilot-readiness track B — payroll correctness for a mid-year real-data pilot, on real Postgres and
/// through the real <c>PayrollController.Process</c> path. Each test computes real payslips end to end.
///
/// <list type="bullet">
/// <item><b>GOSI, one source:</b> an admin typing "9" for a rate is refused with a coded reason; the
/// payslip, the per-employee preview and the readiness report reach the same GOSI for the same wage,
/// from the same effective-dated <c>statutory_rules</c> rows.</item>
/// <item><b>YTD opening balances:</b> a carried opening balance and a payslip this product locked
/// for the same month are never both counted.</item>
/// <item><b>EOSB carried service:</b> three years carried in plus one year here is a four-year award.</item>
/// </list>
/// </summary>
[Collection("Integration")]
public class PilotPayrollCorrectnessPostgresTests
{
    private readonly PostgresFixture _fx;
    public PilotPayrollCorrectnessPostgresTests(PostgresFixture fx) => _fx = fx;

    // ═══════════════════════════════════════════════════════════════════════════
    //  1. GOSI — one source, one unit
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Gosi_AdminTypingNine_IsRefusedWithACodedReason_AndNothingIsSaved()
    {
        await using var db = _fx.CreateDb();
        await StatutoryRuleSeeder.SeedAsync(db, NullLogger.Instance);
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);

        var ctrl = new StatutoryRulesController(db)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Principal(tenantId) } },
        };
        var result = await ctrl.Create(new CreateStatutoryRuleRequest(
            CountryCodes.Saudi, Jurisdictions.KsaMainland, RuleKeys.GosiSaudiEmployeeRate,
            "9", "decimal", "Typed from the GOSI circular", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), null),
            CancellationToken.None);

        var bad = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        Prop<string>(bad.Value!, "code").Should().Be(StatutoryValueUnits.UnitRefusalCode,
            "a client must be able to branch on WHY the value was refused, not parse prose");
        Prop<string>(bad.Value!, "message").Should().Contain("0.09").And.Contain("Nothing has been saved");

        (await db.StatutoryRules.IgnoreQueryFilters().AnyAsync(r => r.TenantId == tenantId))
            .Should().BeFalse("a refused rate must not be written anywhere");

        // The retired GOSI rate list refuses too — with its own code — instead of storing a rate nothing reads.
        var gosi = new GosiController(db, TestReconciliation.For(db), new StatutoryRuleReader(db))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Principal(tenantId) } },
        };
        var retired = await gosi.CreateContributionRule(new CreateGosiRuleRequest(
            GosiClassifications.Saudi, GosiBranches.Annuities, GosiPayers.Employee, 9m,
            new DateOnly(2026, 1, 1), SourceReference: "circular"), CancellationToken.None);
        var gone = retired.Should().BeOfType<ObjectResult>().Subject;
        gone.StatusCode.Should().Be(StatusCodes.Status410Gone);
        Prop<string>(gone.Value!, "code").Should().Be(GosiController.GosiRateStoreRetiredCode);
        Prop<string>(gone.Value!, "statutoryRuleKey").Should().Be(RuleKeys.GosiSaudiEmployeeRate);
        (await db.GosiContributionRules.IgnoreQueryFilters().AnyAsync(r => r.TenantId == tenantId)).Should().BeFalse();
    }

    /// <summary>
    /// The same wage through the payslip, the preview and the readiness report, all reading the
    /// platform statutory rules seeded exactly as production seeds them. 12,500 covered: 9.75% EE
    /// (1,218.75) and 11.75% ER (1,468.75). 60,000 covered is capped at the 45,000 ceiling: 4,387.50 /
    /// 5,287.50. An expatriate pays nothing; the employer pays 2% occupational hazards.
    /// </summary>
    [Theory]
    [InlineData("Saudi",  10_000, 2_500, 1_218.75, 1_468.75)]
    [InlineData("Saudi",  50_000, 10_000, 4_387.50, 5_287.50)]
    [InlineData("Egypt",  10_000, 2_500, 0, 250.00)]
    public async Task Gosi_PayslipPreviewAndReadinessReport_ReachTheSameFigure(
        string nationality, double basicD, double housingD, double expectedEeD, double expectedErD)
    {
        decimal basic = (decimal)basicD, housing = (decimal)housingD;
        decimal expectedEe = (decimal)expectedEeD, expectedEr = (decimal)expectedErD;

        await using var db = _fx.CreateDb();
        await StatutoryRuleSeeder.SeedAsync(db, NullLogger.Instance);
        await GosiRuleSeeder.SeedDefaultsAsync(db, NullLogger.Instance);
        var reader = new StatutoryRuleReader(db);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var (tenantId, company) = await SeedTenantAndCompany(db);
        var employee = await SeedEmployee(db, tenantId, company, nationality, basic, housing,
            joining: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var run = await NewRun(db, tenantId, company, today.Year, today.Month);

        (await Payroll(db, tenantId, reader).Process(run.Id, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();

        var gosiLines = await db.PayrollDeductions.AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.PayrollRunId == run.Id && d.EmployeeId == employee.Id
                     && d.ComponentCode.StartsWith("GOSI"))
            .ToListAsync();
        var payslipEe = gosiLines.Where(d => !d.IsEmployerContribution).Sum(d => d.Amount);
        var payslipEr = gosiLines.Where(d => d.IsEmployerContribution).Sum(d => d.Amount);

        payslipEe.Should().Be(expectedEe, "the payslip deducts the statutory employee share");
        payslipEr.Should().Be(expectedEr, "the payslip accrues the statutory employer share");

        // The per-employee preview.
        var gosi = new GosiController(db, new GosiReconciliationService(db, new _PilotKsaResolver(reader)), reader)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Principal(tenantId) } },
        };
        var readiness = (await gosi.GetEmployeeReadiness(employee.Id, CancellationToken.None))
            .Should().BeOfType<OkObjectResult>().Subject.Value!;
        var preview = readiness.GetType().GetProperty("contributionPreview")!.GetValue(readiness);
        preview.Should().NotBeNull("a ready employee gets a contribution preview");
        Prop<decimal>(preview!, "EmployeeTotal").Should().Be(payslipEe, "the preview is computed by the payslip's engine");
        Prop<decimal>(preview!, "EmployerTotal").Should().Be(payslipEr);

        // The readiness report — the figure a finance team reconciles against the GOSI portal.
        var report = await new GosiReadinessReportService(db, reader).BuildAsync(tenantId, CancellationToken.None);
        var row = report.Employees.Single(e => e.EmployeeId == employee.Id);
        row.EmployeeContributionTotal.Should().Be(payslipEe);
        row.EmployerContributionTotal.Should().Be(payslipEr);
        row.Lines.Should().OnlyContain(l => l.Rate > 0m && l.Rate <= StatutoryValueUnits.MaxContributionRateFraction,
            "every line states the FRACTION the payslip applied");
    }

    /// <summary>
    /// The divergence the second store made possible: a tenant row in gosi_contribution_rules (written
    /// before this change, or by hand) used to move the preview and the readiness report but never the
    /// payslip. It must now move nothing.
    /// </summary>
    [Fact]
    public async Task Gosi_AStaleRowInTheRetiredStore_NoLongerMovesThePreview()
    {
        await using var db = _fx.CreateDb();
        await StatutoryRuleSeeder.SeedAsync(db, NullLogger.Instance);
        await GosiRuleSeeder.SeedDefaultsAsync(db, NullLogger.Instance);
        var reader = new StatutoryRuleReader(db);
        var (tenantId, company) = await SeedTenantAndCompany(db);
        var employee = await SeedEmployee(db, tenantId, company, "Saudi", 10_000m, 2_500m,
            joining: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        db.GosiContributionRules.Add(new GosiContributionRule
        {
            TenantId = tenantId, CountryCode = "SA", Classification = GosiClassifications.Saudi,
            Branch = GosiBranches.Annuities, Payer = GosiPayers.Employee, Rate = 0.20m,
            EffectiveFrom = new DateOnly(2020, 1, 1), IsActive = true, SourceReference = "stale override",
        });
        await db.SaveChangesAsync();

        var report = await new GosiReadinessReportService(db, reader).BuildAsync(tenantId, CancellationToken.None);
        report.Employees.Single(e => e.EmployeeId == employee.Id).EmployeeContributionTotal
            .Should().Be(1_218.75m, "the statutory 9% + 0.75% applies, not the stale 20% row nobody pays on");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  3. YTD opening balances — one period, one source
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The customer goes live on 1 September 2026. The old system's year-to-date as at 31 August is
    /// carried in (100,000 gross). The consultant also ran and locked AUGUST in this product as a
    /// parallel run. August is inside the 100,000 already, so September's YTD is 100,000 + September —
    /// not 100,000 + August + September.
    /// </summary>
    [Fact]
    public async Task Ytd_CarriedBalanceAndAPreCutoverRun_CountAugustOnce()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, company) = await SeedTenantAndCompany(db);
        var employee = await SeedEmployee(db, tenantId, company, "Saudi", 10_000m, 2_500m,
            joining: new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var rules = KsaRules();

        var august = await ProcessAndLock(db, tenantId, company, rules, 2026, 8);
        var augustSlip = await Slip(db, august.Id, employee.Id);

        db.CompanyCutovers.Add(new CompanyCutover
        {
            TenantId = tenantId, CompanyId = company.Id, CutoverDate = new DateOnly(2026, 9, 1),
            SourceSystem = "SAP", Status = CutoverStatuses.Active,
        });
        AddOpeningYtd(db, tenantId, company, employee, gross: 100_000m, deductions: 9_750m, net: 90_250m);
        await db.SaveChangesAsync();

        var september = await NewRun(db, tenantId, company, 2026, 9);
        (await Payroll(db, tenantId, rules).Process(september.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        var sepSlip = await Slip(db, september.Id, employee.Id);

        sepSlip.YtdGross.Should().Be(100_000m + sepSlip.GrossSalary,
            "the carried 100,000 already contains August; the August payslip must not be added again");
        sepSlip.YtdGross.Should().NotBe(100_000m + augustSlip.GrossSalary + sepSlip.GrossSalary);
        sepSlip.YtdDeductions.Should().Be(9_750m + sepSlip.Deductions);
        sepSlip.YtdNet.Should().Be(90_250m + sepSlip.NetSalary);

        (await Findings(db, september.Id)).Should().Contain(f =>
            f.Code == PayrollYtdBasis.PreCutoverExcludedCode && f.Severity == "Warning" && f.EmployeeId == employee.Id,
            "the preparer is told which payslips the opening balance stands in for");

        // The month after: September is in-product and counts; August still does not.
        await db.PayrollRuns.Where(r => r.Id == september.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "Locked"));
        var october = await NewRun(db, tenantId, company, 2026, 10);
        (await Payroll(db, tenantId, rules).Process(october.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        var octSlip = await Slip(db, october.Id, employee.Id);
        octSlip.YtdGross.Should().Be(100_000m + sepSlip.GrossSalary + octSlip.GrossSalary);
    }

    /// <summary>
    /// The other side of the boundary. A run for a month BEFORE the cutover — processed after the
    /// balances were loaded — must not add an opening balance that is stated as at a later date and
    /// already contains that very month.
    /// </summary>
    [Fact]
    public async Task Ytd_ARunBeforeTheCutover_DoesNotAddTheLaterOpeningBalance()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, company) = await SeedTenantAndCompany(db);
        var employee = await SeedEmployee(db, tenantId, company, "Saudi", 10_000m, 2_500m,
            joining: new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        db.CompanyCutovers.Add(new CompanyCutover
        {
            TenantId = tenantId, CompanyId = company.Id, CutoverDate = new DateOnly(2026, 9, 1),
            SourceSystem = "SAP", Status = CutoverStatuses.Active,
        });
        AddOpeningYtd(db, tenantId, company, employee, gross: 100_000m, deductions: 9_750m, net: 90_250m);
        await db.SaveChangesAsync();

        var august = await NewRun(db, tenantId, company, 2026, 8);
        (await Payroll(db, tenantId, KsaRules()).Process(august.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        var slip = await Slip(db, august.Id, employee.Id);

        slip.YtdGross.Should().Be(slip.GrossSalary,
            "the 31 August opening balance already includes August; adding it to an August payslip double-counts August");
    }

    /// <summary>
    /// No cutover declared, but carried YTD and this product's locked payslips both exist for the year.
    /// Nothing says where the carried figures end, so the run is blocked with a coded reason — by
    /// Process and, identically, by /validate.
    /// </summary>
    [Fact]
    public async Task Ytd_NoCutover_BothSourcesPresent_IsBlockedNotGuessed()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, company) = await SeedTenantAndCompany(db);
        var employee = await SeedEmployee(db, tenantId, company, "Saudi", 10_000m, 2_500m,
            joining: new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var rules = KsaRules();

        await ProcessAndLock(db, tenantId, company, rules, 2026, 8);
        AddOpeningYtd(db, tenantId, company, employee, gross: 100_000m, deductions: 9_750m, net: 90_250m);
        await db.SaveChangesAsync();

        var september = await NewRun(db, tenantId, company, 2026, 9);
        (await Payroll(db, tenantId, rules).Process(september.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        (await Findings(db, september.Id)).Should().Contain(f =>
            f.Code == PayrollYtdBasis.UnresolvedOverlapCode && f.Severity == "Error" && f.EmployeeId == employee.Id);

        (await Payroll(db, tenantId, rules).Validate(september.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        (await Findings(db, september.Id)).Should().Contain(f =>
            f.Code == PayrollYtdBasis.UnresolvedOverlapCode && f.Severity == "Error",
            "/validate replaces the findings wholesale and must re-derive the same block");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  4. EOSB — carried prior service
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Three years with the previous system (carried as PriorServiceStartDate 2022-09-01) plus one year
    /// here (joined this product 2025-09-01), terminated 2026-08-31: the Art. 84 award is the FOUR-year
    /// award — identical to an employee who joined this product on 2022-09-01 — and not the one-year one.
    /// </summary>
    [Fact]
    public async Task Eosb_ThreeYearsCarriedPlusOneHere_IsTheFourYearAward()
    {
        await using var db = _fx.CreateDb();
        var (tenantId, company) = await SeedTenantAndCompany(db);
        db.GCCComplianceSettings.Add(new GCCComplianceSetting { TenantId = tenantId, CountryCode = company.CountryCode, EosbEnabled = true });
        await db.SaveChangesAsync();

        var migrated = await SeedEmployee(db, tenantId, company, "Saudi", 10_000m, 2_000m,
            joining: new DateTime(2025, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        var native = await SeedEmployee(db, tenantId, company, "Saudi", 10_000m, 2_000m,
            joining: new DateTime(2022, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        var oneYear = await SeedEmployee(db, tenantId, company, "Saudi", 10_000m, 2_000m,
            joining: new DateTime(2025, 9, 1, 0, 0, 0, DateTimeKind.Utc));

        db.EmployeeEosbOpeningBalances.Add(new EmployeeEosbOpeningBalance
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeId = migrated.Id, EmployeeCode = migrated.EmployeeCode,
            AsAtDate = new DateOnly(2025, 8, 31), PriorServiceStartDate = new DateOnly(2022, 9, 1),
            AccruedMonths = 36m, AccruedAmount = 18_000m, Currency = "SAR", SourceSystem = "SAP", SourceRecordId = "EOSB-PILOT-1",
        });
        await db.SaveChangesAsync();

        var leave = new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);
        var rules = KsaRules();
        var migratedAward = await Eosb(db, tenantId, rules, migrated.Id, leave);
        var nativeAward = await Eosb(db, tenantId, rules, native.Id, leave);
        var oneYearAward = await Eosb(db, tenantId, rules, oneYear.Id, leave);

        migratedAward.Should().Be(nativeAward,
            "3 years carried + 1 year here is one continuous four-year service period (Art. 84)");
        migratedAward.Should().BeApproximately(4m * 0.5m * 12_000m, 0.005m * 24_000m,
            "four years at half a month of the 12,000 last wage is about 24,000");
        oneYearAward.Should().BeLessThan(migratedAward / 3m,
            "without the carried service the award would be the one-year figure");
    }

    // ══════════════════════ Fixture ══════════════════════

    private static async Task<(Guid TenantId, Company Company)> SeedTenantAndCompany(ZayraDbContext db)
    {
        var tenantId = await PostgresFixture.SeedMinimalTenant(db);
        var company = new Company
        {
            TenantId = tenantId,
            LegalNameEn = $"Pilot Co {Guid.NewGuid():N}",
            TradeName = "Pilot Co",
            CountryCode = CountryCodes.Saudi,
            Jurisdiction = Jurisdictions.KsaMainland,
            RegistrationNumber = $"PIL-{Guid.NewGuid():N}",
            DefaultCurrency = "SAR",
            IsActive = true,
            GosiEmployerId = "GOSI-ER-1",
            CreatedAtUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        return (tenantId, company);
    }

    private static async Task<Employee> SeedEmployee(
        ZayraDbContext db, Guid tenantId, Company company, string nationality, decimal basic, decimal housing, DateTime joining)
    {
        var employee = new Employee
        {
            TenantId = tenantId,
            CompanyId = company.Id,
            EmployeeCode = $"PIL-{Guid.NewGuid():N}"[..16],
            FullName = "Pilot Employee",
            Nationality = nationality,
            Status = "Active",
            ContractType = "Indefinite",
            JoiningDate = joining,
            CountryCode = CountryCodes.Saudi,
            Salary = basic,
            GosiReference = "GOSI-123456",
            GosiFirstRegisteredOn = new DateOnly(2015, 1, 1),
        };
        db.Employees.Add(employee);
        await db.SaveChangesAsync();

        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId,
            EmployeeId = employee.Id,
            SalaryStructureId = Guid.NewGuid(),
            BasicSalary = basic,
            HousingAllowance = housing,
            TransportAllowance = 0m,
            Currency = "SAR",
            EffectiveDate = DateOnly.FromDateTime(joining),
            IsActive = true,
        });
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
        {
            TenantId = tenantId,
            EmployeeId = employee.Id,
            Iban = "SA4420000001234567891234",
            MolId = $"MOL-{Guid.NewGuid():N}",
            SalaryCurrency = "SAR",
        });
        await db.SaveChangesAsync();
        return employee;
    }

    private static async Task<PayrollRun> NewRun(ZayraDbContext db, Guid tenantId, Company company, int year, int month)
    {
        var run = new PayrollRun
        {
            TenantId = tenantId, CompanyId = company.Id, Year = year, Month = month, Status = "Draft",
            CreatedAtUtc = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        return run;
    }

    /// <summary>Process a month and mark it Locked, as a finished in-product month (or a parallel run) would be.</summary>
    private static async Task<PayrollRun> ProcessAndLock(
        ZayraDbContext db, Guid tenantId, Company company, IStatutoryRuleReader rules, int year, int month)
    {
        var run = await NewRun(db, tenantId, company, year, month);
        (await Payroll(db, tenantId, rules).Process(run.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        await db.PayrollRuns.Where(r => r.Id == run.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "Locked"));
        return run;
    }

    private static void AddOpeningYtd(ZayraDbContext db, Guid tenantId, Company company, Employee employee,
        decimal gross, decimal deductions, decimal net)
    {
        foreach (var (type, amount) in new[]
                 {
                     (OpeningBalanceTypes.YtdGross, gross),
                     (OpeningBalanceTypes.YtdDeductions, deductions),
                     (OpeningBalanceTypes.YtdNet, net),
                 })
            db.PayrollOpeningBalances.Add(new PayrollOpeningBalance
            {
                TenantId = tenantId, CompanyId = company.Id, EmployeeId = employee.Id, EmployeeCode = employee.EmployeeCode,
                Year = 2026, BalanceType = type, ComponentCode = "TOTAL", Amount = amount, Currency = "SAR",
                SourceSystem = "SAP", SourceRecordId = $"YTD-{type}",
            });
    }

    private static async Task<PayrollSlip> Slip(ZayraDbContext db, Guid runId, int employeeId) =>
        await db.PayrollSlips.AsNoTracking().SingleAsync(s => s.RunId == runId && s.EmployeeId == employeeId);

    private static async Task<List<PayrollValidationResult>> Findings(ZayraDbContext db, Guid runId) =>
        await db.PayrollValidationResults.AsNoTracking().Where(r => r.PayrollRunId == runId).ToListAsync();

    private static async Task<decimal> Eosb(ZayraDbContext db, Guid tenantId, IStatutoryRuleReader rules, int employeeId, DateTime asOf)
    {
        var result = await Payroll(db, tenantId, rules).CalculateEosb(
            new EosbCalculationRequest(employeeId, asOf, "Termination"), CancellationToken.None);
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        return Prop<decimal>(ok.Value!, "eosbAmount");
    }

    private static StubRuleReader KsaRules() => new StubRuleReader()
        .Set("ot.hourly_base", "wage")
        .Set("ot.standard_multiplier", 1.5m)
        .Set("ot.restday_multiplier", 2.0m)
        .Set("ot.holiday_multiplier", 2.0m)
        .Set("ot.standard_monthly_hours", 240m)
        .Set("lop.monthly_day_divisor", 30m)
        .Set("lop.standard_work_minutes_per_day", 480m)
        .Set("gosi.saudi_employee_rate", 0.09m)
        .Set("gosi.saudi_employer_rate", 0.09m)
        .Set("gosi.saned_rate", 0.0075m)
        .Set("gosi.expat_occupational_hazard_rate", 0.02m)
        .Set("gosi.covered_wage_ceiling_sar", 45_000m);

    private static PayrollController Payroll(ZayraDbContext db, Guid tenantId, IStatutoryRuleReader rules)
    {
        var ctrl = new PayrollController(
            db,
            new _PilotScope(),
            new HttpContextAccessor(),
            new _PilotNotifications(),
            new _PilotKsaResolver(rules),
            rules,
            new _PilotLetters(),
            new NullDocumentStorage(),
            new PdfRenderGate(8));
        ctrl.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Principal(tenantId) } };
        return ctrl;
    }

    private static ClaimsPrincipal Principal(Guid tenantId) => new(new ClaimsIdentity(new[]
    {
        new Claim("tenant_id", tenantId.ToString()),
        new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        new Claim(ClaimTypes.Name, "Pilot Tester"),
        new Claim(ClaimTypes.Role, "Admin"),
        new Claim("permission", "payroll.read"),
        new Claim("permission", "payroll.write"),
        new Claim("permission", "payroll.rates.statutory_override"),
    }, "Test"));

    private static T Prop<T>(object value, string name) =>
        (T)value.GetType().GetProperty(name)!.GetValue(value)!;
}

// ── File-scoped stubs ─────────────────────────────────────────────────────────

file sealed class _PilotScope : IDataScopeService
{
    public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct)
        => Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
}

file sealed class _PilotNotifications : INotificationService
{
    public Task NotifyAsync(Guid t, Guid? u, string title, string msg, string en, string? eid, CancellationToken ct) => Task.CompletedTask;
    public Task SendEmailAsync(Guid t, string code, string to, string name, Dictionary<string, string> vars, CancellationToken ct) => Task.CompletedTask;
}

file sealed class _PilotKsaResolver : ICountryPackResolver
{
    private readonly IStatutoryRuleReader _rules;
    public _PilotKsaResolver(IStatutoryRuleReader rules) => _rules = rules;

    public IStatutoryDeductionCalculator ResolveDeductionCalculator(string cc, string j) => new KsaDeductionCalculator(_rules);
    public IEndOfServiceCalculator ResolveEndOfServiceCalculator(string cc, string j) => new KsaEndOfServiceCalculator(_rules);
    public IWageProtectionExporter ResolveWageProtectionExporter(string cc, string j) => new DefaultWageProtectionExporter();
    public INationalizationTracker ResolveNationalizationTracker(string cc, string j) => new DefaultNationalizationTracker();
    public ILocalizationProfile ResolveLocalizationProfile(string cc, string j) => new DefaultLocalizationProfile();
    public ICountryPackDescriptor ResolveDescriptor(string cc, string j) => new DefaultCountryPackDescriptor();
}

file sealed class _PilotLetters : ILetterService
{
    public Task<byte[]> GeneratePayslipPdfAsync(PayslipData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateAppointmentLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateExperienceLetterAsync(LetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<byte[]> GenerateOfferLetterAsync(OfferLetterData d, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
}
