using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Zayra.Api.Application.Common;
using Zayra.Api.Controllers;
using Zayra.Api.Data;
using Zayra.Api.Infrastructure.Audit;
using Zayra.Api.Infrastructure.Finance;
using Zayra.Api.Infrastructure.Organization;
using Zayra.Api.Infrastructure.Payroll;
using Zayra.Api.Infrastructure.Policy;
using Zayra.Api.Infrastructure.Seed;
using Zayra.Api.Models;

namespace Zayra.Api.Tests;

/// <summary>
/// The overtime money proof: ONE dataset, driven through the real doors, ties to the cent from the approval to the payslip line,
/// to the GL accrual journal, to the ERP export file. Slip = ledger = export.
///
/// <para>The dataset (KSA, basic 12,000 + housing 3,000, 240 h month; Art. 107 hour = hourly wage + 50% of hourly basic):</para>
/// <list type="bullet">
///   <item>Wed 10 Jun 2026, 120 min on an ordinary day: 62.50 + 0.5 x 50.00 = 87.50 an hour, 175.00.</item>
///   <item>Fri 12 Jun 2026 (rest day; company rest-day premium 2.0), 180 min: 62.50 + 1.0 x 50.00 = 112.50 an hour, 337.50.</item>
///   <item>Thu 28 May 2026, 120 min, approved AFTER May's run locked (late_approval_mode = BackPay): carried into June as back pay, 175.00.</item>
/// </list>
/// <para>June's overtime expense is therefore 175.00 + 337.50 + 175.00 = 687.50: on the payslip, as debits to 5005 in the
/// accrual journal, and as 5005 debit lines in the ERP file. The figures are worked by hand from the statute, not read back
/// from the engine.</para>
/// </summary>
public class OvertimeMoneyProofTests
{
    private const decimal Basic = 12_000m;
    private const decimal Housing = 3_000m;
    private static readonly Guid Preparer = Guid.NewGuid();
    private static readonly Guid Approver = Guid.NewGuid();
    private static readonly Guid HrAdmin = Guid.NewGuid();

    private static StubRuleReader Rules() => new StubRuleReader()
        .Set("gosi.saudi_employee_rate", 0.09m).Set("gosi.saudi_employer_rate", 0.09m).Set("gosi.saned_rate", 0.0075m)
        .Set("gosi.expat_occupational_hazard_rate", 0.02m).Set("gosi.covered_wage_ceiling_sar", 45_000m)
        .Set("ot.standard_multiplier", 1.5m).Set("ot.restday_multiplier", 2.0m).Set("ot.holiday_multiplier", 2.0m)
        .Set("ot.standard_monthly_hours", 240m)
        .Set("lop.monthly_day_divisor", 30m).Set("lop.standard_work_minutes_per_day", 480m);

    private static PayrollController Payroll(ZayraDbContext db, Guid tenantId, Guid userId)
    {
        var rules = Rules();
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()), new(ClaimTypes.NameIdentifier, userId.ToString()), new(ClaimTypes.Name, $"user-{userId:N}"[..12]),
            new(ClaimTypes.Role, "Payroll Manager"),
            new("permission", "payroll.read"), new("permission", "payroll.write"), new("permission", "payroll.approve"),
            new("permission", "payroll.lock"), new("permission", "payroll.export"),
        };
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        return new PayrollController(db, new OrgScope(), new HttpContextAccessor { HttpContext = http }, new F2NullNotifications(),
            new KsaTestPackResolver(rules), rules, new F2NullLetters(), new NullDocumentStorage(), new Zayra.Api.Infrastructure.Documents.PdfRenderGate(4))
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private static OvertimeController Overtime(ZayraDbContext db, Guid tenantId)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()), new(ClaimTypes.NameIdentifier, HrAdmin.ToString()), new(ClaimTypes.Role, "Admin"),
            new("permission", "overtime.approve"), new("permission", "overtime.read"),
        };
        return new OvertimeController(db, new OrgScope(), new HrmHierarchyService(db, new AuditService(db)), ruleReader: Rules())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) } },
        };
    }

    private static async Task Ok(Task<IActionResult> call, string step)
    {
        var result = await call;
        if (result is ObjectResult { StatusCode: >= 400 } bad)
            Assert.Fail($"{step} refused: HTTP {bad.StatusCode} {JsonSerializer.Serialize(bad.Value)}");
        result.Should().BeAssignableTo<OkObjectResult>(step);
    }

    private static async Task CloseRunAsync(ZayraDbContext db, Guid tenantId, Guid runId)
    {
        await Ok(Payroll(db, tenantId, Preparer).Process(runId, CancellationToken.None), "Process");
        db.ChangeTracker.Clear();
        // Approval (maker-checker, reconciliation sign-off) is not what this proof is about and has its own suites: nothing in
        // the run may be a blocking error, then the run is put in the Approved state Lock posts from, as PayComponentNetPayDefectTests does.
        var errors = await db.PayrollValidationResults.AsNoTracking()
            .Where(v => v.PayrollRunId == runId && v.Severity == "Error").Select(v => v.Code).ToListAsync();
        errors.Should().BeEmpty();
        await db.PayrollValidationResults.Where(v => v.PayrollRunId == runId).ExecuteDeleteAsync();
        await db.PayrollRuns.Where(r => r.Id == runId).ExecuteUpdateAsync(x => x.SetProperty(r => r.Status, "Approved"));
        db.ChangeTracker.Clear();
        await Ok(Payroll(db, tenantId, Approver).Lock(runId, CancellationToken.None), "Lock");
        db.ChangeTracker.Clear();
    }

    private static OvertimeRequest Request(Guid tenantId, Employee e, Guid policyId, DateOnly day, int fromHour, int minutes) => new()
    {
        TenantId = tenantId, CompanyId = e.CompanyId, EmployeeId = e.Id, EmployeeName = e.FullName, OvertimePolicyId = policyId, WorkDate = day,
        StartTimeUtc = day.ToDateTime(new TimeOnly(fromHour, 0), DateTimeKind.Utc),
        EndTimeUtc = day.ToDateTime(new TimeOnly(fromHour, 0), DateTimeKind.Utc).AddMinutes(minutes),
        RequestedMinutes = minutes, Reason = "Month-end close", Source = "Manual", Status = "PendingHR", CreatedBy = Guid.NewGuid(),
    };

    [Fact]
    public async Task ApprovedOvertime_PayslipLine_EqualsTheGlJournal_EqualsTheErpExport_ToTheCent()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        await using var _ = conn;
        await using var db = new ZayraDbContext(new DbContextOptionsBuilder<ZayraDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();

        // ── the company, the employee and the package ────────────────────────────────────────────
        var tenantId = Guid.NewGuid();
        var company = new Company
        {
            TenantId = tenantId, LegalNameEn = "Proof Co", CountryCode = "SAU", Jurisdiction = "KSA-mainland",
            RegistrationNumber = $"REG-{Guid.NewGuid():N}", DefaultCurrency = "SAR", IsActive = true,
        };
        db.Companies.Add(company);
        var structure = new SalaryStructure { TenantId = tenantId, Code = "STR", Name = "Base", Currency = "SAR", EffectiveDate = new DateOnly(2025, 1, 1), IsActive = true };
        db.SalaryStructures.Add(structure);
        await db.SaveChangesAsync();
        var emp = new Employee
        {
            TenantId = tenantId, CompanyId = company.Id, EmployeeCode = "E-PROOF-1", FullName = "Proof Employee", Nationality = "SAU",
            Status = "Active", JoiningDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), ContractType = "Indefinite",
        };
        db.Employees.Add(emp);
        await db.SaveChangesAsync();
        db.EmployeeSalaryStructures.Add(new EmployeeSalaryStructure
        {
            TenantId = tenantId, EmployeeId = emp.Id, SalaryStructureId = structure.Id, BasicSalary = Basic, HousingAllowance = Housing,
            Currency = "SAR", EffectiveDate = new DateOnly(2024, 1, 1), IsActive = true,
        });
        db.EmployeePayrollProfiles.Add(new EmployeePayrollProfile
        {
            TenantId = tenantId, EmployeeId = emp.Id, Iban = "SA4420000001234567891234", MolId = "MOL-PROOF-1", SalaryCurrency = "SAR",
        });
        var policy = new OvertimePolicy
        {
            TenantId = tenantId, Code = "STD", Name = "Standard", IsActive = true, RoundingRule = "None", HourlyRateBasis = "BasicSalary",
            StandardMonthlyHours = 240, MinimumMinutes = 0, MaximumMinutesPerDay = 720, MonthlyCapMinutes = 6000,
        };
        db.OvertimePolicies.Add(policy);
        db.SystemSettings.Add(new SystemSetting
        {
            TenantId = tenantId, Category = PolicyRegistry.StorageCategory, SettingKey = PolicyRegistry.OvertimeLateApprovalMode, SettingValue = "BackPay",
        });
        await db.SaveChangesAsync();
        await GlDriverSeeder.SeedTenantDefaultsAsync(db, tenantId, CancellationToken.None);
        await db.SaveChangesAsync();

        // ── May is paid and locked before its late overtime is approved ──────────────────────────
        var may = new PayrollRun { TenantId = tenantId, CompanyId = company.Id, Year = 2026, Month = 5, Status = "Draft", CreatedByUserId = Preparer };
        db.PayrollRuns.Add(may);
        await db.SaveChangesAsync();
        await CloseRunAsync(db, tenantId, may.Id);

        var june = new PayrollRun { TenantId = tenantId, CompanyId = company.Id, Year = 2026, Month = 6, Status = "Draft", CreatedByUserId = Preparer };
        db.PayrollRuns.Add(june);
        var regular = Request(tenantId, emp, policy.Id, new DateOnly(2026, 6, 10), 17, 120);
        var restDay = Request(tenantId, emp, policy.Id, new DateOnly(2026, 6, 12), 9, 180);
        var late = Request(tenantId, emp, policy.Id, new DateOnly(2026, 5, 28), 17, 120);
        db.OvertimeRequests.AddRange(regular, restDay, late);
        await db.SaveChangesAsync();
        new DateOnly(2026, 6, 12).DayOfWeek.Should().Be(DayOfWeek.Friday, "premise: the rest-day entry is on a Friday");

        // ── HR's final approval, through the real door (Calculate, impacts, the late-approval carry) ─
        foreach (var r in new[] { regular, restDay, late })
        {
            db.ChangeTracker.Clear();
            var approved = await Overtime(db, tenantId).Approve(r.Id, new OvertimeDecisionRequest(0, "Timesheet received after the May close"), CancellationToken.None);
            if (approved is ObjectResult { StatusCode: >= 400 } bad)
                Assert.Fail($"Overtime approve refused: HTTP {bad.StatusCode} {JsonSerializer.Serialize(bad.Value)}");
        }
        db.ChangeTracker.Clear();
        (await db.OvertimePayrollImpacts.AsNoTracking().SingleAsync(i => i.OvertimeRequestId == late.Id)).Status
            .Should().Be("CarriedForward", "premise: May was locked, so the late entry is carried into June as back pay");

        // ── June: process, approve, lock ─────────────────────────────────────────────────────────
        await CloseRunAsync(db, tenantId, june.Id);

        // 1. The payslip lines.
        var lines = await db.PayrollEarnings.AsNoTracking().Where(e => e.PayrollRunId == june.Id && e.EmployeeId == emp.Id).ToListAsync();
        var overtimeLine = lines.Where(l => l.ComponentCode == "OVERTIME").Sum(l => l.Amount);
        var backPayLine = lines.Where(l => l.ComponentCode == "ADJ_OVERTIME_BACK_PAY").Sum(l => l.Amount);
        overtimeLine.Should().Be(512.50m, "175.00 for the ordinary day + 337.50 for the rest day, both Art. 107");
        backPayLine.Should().Be(175.00m, "the late May entry, priced at approval, paid once in June");
        var slipOvertime = overtimeLine + backPayLine;
        slipOvertime.Should().Be(687.50m);
        lines.Where(l => l.ComponentCode is "OVERTIME" or "ADJ_OVERTIME_BACK_PAY")
            .Select(l => PayrollRunProcessor.EarningDriverKey(l.ComponentCode, l.Source)).Should().OnlyContain(k => k == "EARN:OVERTIME");

        // 2. The ledger: the accrual journal Lock posted for June.
        var gl = await db.FinanceGlEntries.AsNoTracking()
            .Where(g => g.TenantId == tenantId && g.SourceModule == "Payroll" && g.SourceEntityId == june.Id && g.EventType == GlEventTypes.Accrual)
            .ToListAsync();
        var ledgerOvertime = gl.Where(g => g.DebitAccount.StartsWith("5005")).Sum(g => g.Amount)
                             - gl.Where(g => g.CreditAccount.StartsWith("5005")).Sum(g => g.Amount);
        ledgerOvertime.Should().Be(slipOvertime, "the overtime expense in the ledger is exactly what the payslip paid");
        gl.Where(g => !string.IsNullOrEmpty(g.DebitAccount)).Sum(g => g.Amount)
            .Should().Be(gl.Where(g => !string.IsNullOrEmpty(g.CreditAccount)).Sum(g => g.Amount), "the journal balances");

        // 3. The ERP export of that run.
        var (build, refusal) = await new JournalExportService(db, JournalExportFormatterGoldenTests.AllFormatters())
            .BuildAsync(tenantId, new JournalExportFilter(PayrollRunId: june.Id, CompanyId: company.Id), "generic-csv", CancellationToken.None);
        refusal.Should().BeNull(refusal is null ? "" : JsonSerializer.Serialize(refusal.Payload));
        var exportOvertime = build!.Document.Lines.Where(l => l.AccountCode == "5005").Sum(l => l.Debit - l.Credit);
        exportOvertime.Should().Be(slipOvertime, "the ERP receives exactly the overtime expense the ledger holds");
        var csv = Encoding.UTF8.GetString(build.Bytes);
        csv.Should().Contain(",5005,", "the file itself carries the overtime expense account");

        // Paid once: every overtime impact is settled, and nothing is left for a later run.
        (await db.OvertimePayrollImpacts.AsNoTracking().CountAsync(i => i.TenantId == tenantId && i.Status == "PendingPayroll")).Should().Be(0);
        (await db.PayrollEarnings.AsNoTracking().Where(e => e.PayrollRunId == may.Id && (e.ComponentCode == "OVERTIME" || e.ComponentCode == "ADJ_OVERTIME_BACK_PAY")).CountAsync())
            .Should().Be(0, "May was closed before any overtime was approved");
    }

    private sealed class OrgScope : IDataScopeService
    {
        public Task<DataScope> ResolveAsync(ClaimsPrincipal caller, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(new DataScope { Level = DataScopeLevel.Organization });
    }
}
